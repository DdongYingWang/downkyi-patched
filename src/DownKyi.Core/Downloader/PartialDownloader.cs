using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace DownKyi.Core.Downloader
{
    /// <summary>
    /// 下载错误事件参数
    /// </summary>
    public class DownloadErrorEventArgs : EventArgs
    {
        public DownloadErrorEventArgs(Exception exception)
        {
            Exception = exception;
        }

        /// <summary>
        /// 引发本次失败的异常
        /// </summary>
        public Exception Exception { get; }
    }

    /// <summary>
    /// 部分下载器
    /// </summary>
    public class PartialDownloader
    {
        /// <summary>
        /// 这部分完成事件
        /// </summary>
        public event EventHandler DownloadPartCompleted;

        /// <summary>
        /// 部分下载进度改变事件
        /// </summary>
        public event EventHandler DownloadPartProgressChanged;

        /// <summary>
        /// 部分下载停止事件
        /// </summary>
        public event EventHandler DownloadPartStopped;

        /// <summary>
        /// 这部分下载失败事件。
        /// 下载线程内的任何异常都通过该事件上报，绝不允许异常从后台线程逃逸，
        /// 否则会成为未处理异常直接终止整个进程（CLR20r3）。
        /// </summary>
        public event EventHandler<DownloadErrorEventArgs> DownloadPartFailed;

        private readonly AsyncOperation _aop = AsyncOperationManager.CreateOperation(null);
        private readonly int[] _lastSpeeds;
        private long _counter;
        private long _to;
        private long _totalBytesRead;
        private bool _wait;

        /// <summary>
        /// 下载已停止
        /// </summary>
        public bool Stopped { get; private set; }

        /// <summary>
        /// 下载已完成
        /// </summary>
        public bool Completed { get; private set; }

        /// <summary>
        /// 下载已失败
        /// </summary>
        public bool Failed { get; private set; }

        /// <summary>
        /// 最后一次失败的异常
        /// </summary>
        public Exception LastError { get; private set; }

        /// <summary>
        /// 下载进度
        /// </summary>
        public int Progress { get; private set; }

        /// <summary>
        /// 下载目录
        /// </summary>
        public string Directory { get; }

        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// 已读字节数
        /// </summary>
        public long TotalBytesRead => _totalBytesRead;

        /// <summary>
        /// 内容长度
        /// </summary>
        public long ContentLength { get; private set; }

        /// <summary>
        /// RangeAllowed
        /// </summary>
        public bool RangeAllowed { get; }

        /// <summary>
        /// url
        /// </summary>
        public string Url { get; }

        /// <summary>
        /// to
        /// </summary>
        public long To
        {
            get => _to;
            set
            {
                _to = value;
                ContentLength = _to - From + 1;
            }
        }

        /// <summary>
        /// from
        /// </summary>
        public long From { get; }

        /// <summary>
        /// 当前位置
        /// </summary>
        public long CurrentPosition => From + _totalBytesRead - 1;

        /// <summary>
        /// 剩余字节数
        /// </summary>
        public long RemainingBytes => ContentLength - _totalBytesRead;

        /// <summary>
        /// 完整路径
        /// </summary>
        public string FullPath => Path.Combine(Directory, FileName);

        /// <summary>
        /// 下载速度
        /// </summary>
        public int SpeedInBytes
        {
            get
            {
                if (Completed)
                {
                    return 0;
                }

                int totalSpeeds = _lastSpeeds.Sum();
                return totalSpeeds / 10;
            }
        }

        /// <summary>
        /// 部分块下载
        /// </summary>
        /// <param name="url"></param>
        /// <param name="dir"></param>
        /// <param name="fileGuid"></param>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="rangeAllowed"></param>
        public PartialDownloader(string url, string dir, string fileGuid, long from, long to, bool rangeAllowed)
        {
            From = from;
            _to = to;
            Url = url;
            RangeAllowed = rangeAllowed;
            FileName = fileGuid;
            Directory = dir;
            _lastSpeeds = new int[10];
        }

        private void DownloadProcedure(Action<HttpWebRequest> config)
        {
            // 该方法运行在独立的后台线程上，一旦有异常逃逸就是未处理异常，
            // 会直接杀死整个进程（表现为 CLR20r3 闪退）。
            // 因此这里必须把所有异常收敛成 DownloadPartFailed 事件。
            Exception failure = null;

            try
            {
                // 修复要点 1：
                // 原来使用 FileShare.Delete，等价于"除删除外不允许任何其他句柄访问"，
                // 合并阶段的 File.OpenRead（需要 FileShare.Read）必然与之冲突，
                // 抛出 IOException：另一个进程正在使用此文件。
                // 这里放宽为 Read | Delete，允许合并线程并发读取分片。
                using (var file = new FileStream(FullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
                {
                    var sw = new Stopwatch();
                    if (WebRequest.Create(Url) is HttpWebRequest req)
                    {
                        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.122 Safari/537.36";
                        req.AllowAutoRedirect = true;
                        req.MaximumAutomaticRedirections = 5;
                        req.ServicePoint.ConnectionLimit += 1;
                        req.ServicePoint.Expect100Continue = true;
                        req.ProtocolVersion = HttpVersion.Version11;
                        req.Proxy = WebRequest.GetSystemWebProxy();
                        config(req);
                        if (RangeAllowed)
                        {
                            req.AddRange(From, _to);
                        }

                        try
                        {
                            using (var resp = req.GetResponse() as HttpWebResponse)
                            {
                                if (resp != null)
                                {
                                    ContentLength = resp.ContentLength;
                                    if (ContentLength <= 0 || (RangeAllowed && ContentLength != _to - From + 1))
                                    {
                                        throw new IOException("Invalid response content");
                                    }

                                    using (var tempStream = resp.GetResponseStream())
                                    {
                                        if (tempStream == null)
                                        {
                                            throw new IOException("Empty response stream");
                                        }

                                        int bytesRead;
                                        byte[] buffer = new byte[4096];
                                        sw.Start();
                                        while ((bytesRead = tempStream.Read(buffer, 0, buffer.Length)) > 0)
                                        {
                                            if (_totalBytesRead + bytesRead > ContentLength)
                                            {
                                                bytesRead = (int)(ContentLength - _totalBytesRead);
                                            }

                                            file.Write(buffer, 0, bytesRead);
                                            _totalBytesRead += bytesRead;
                                            _lastSpeeds[_counter] = (int)(_totalBytesRead / Math.Ceiling(sw.Elapsed.TotalSeconds));
                                            _counter = (_counter >= 9) ? 0 : _counter + 1;
                                            int tempProgress = (int)(_totalBytesRead * 100 / ContentLength);
                                            if (Progress != tempProgress)
                                            {
                                                Progress = tempProgress;
                                                var progressHandler = DownloadPartProgressChanged;
                                                if (progressHandler != null)
                                                {
                                                    _aop.Post(state => progressHandler(this, EventArgs.Empty), null);
                                                }
                                            }

                                            if (Stopped || (RangeAllowed && _totalBytesRead == ContentLength))
                                            {
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            req.Abort();
                        }
                    }

                    sw.Stop();

                    // 修复要点 2：
                    // 在释放文件句柄之前先把缓冲区刷盘，
                    // 保证 using 结束后磁盘上的分片内容是完整的。
                    file.Flush();
                }
                // 修复要点 3（本次崩溃的关键）：
                // 完成/停止事件必须在上面的 using 块结束、文件句柄确实释放之后才触发。
                // 原实现把事件触发写在 using 块内部，导致 MergeParts 打开分片文件时
                // 写句柄仍然存活，直接抛出 IOException 并崩溃进程。
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure != null)
            {
                Failed = true;
                LastError = failure;

                var failedHandler = DownloadPartFailed;
                if (failedHandler != null)
                {
                    _aop.Post(state => failedHandler(this, new DownloadErrorEventArgs(failure)), null);
                }

                return;
            }

            if (!Stopped)
            {
                Completed = true;

                var completedHandler = DownloadPartCompleted;
                if (completedHandler != null)
                {
                    _aop.Post(state => completedHandler(this, EventArgs.Empty), null);
                }
            }
            else
            {
                var stoppedHandler = DownloadPartStopped;
                if (stoppedHandler != null)
                {
                    _aop.Post(state => stoppedHandler(this, EventArgs.Empty), null);
                }
            }
        }

        /// <summary>
        /// 启动下载
        /// </summary>
        public void Start(Action<HttpWebRequest> config)
        {
            Stopped = false;
            var procThread = new Thread(_ => DownloadProcedure(config))
            {
                // 后台线程，避免下载线程卡住时阻塞主程序退出
                IsBackground = true
            };
            procThread.Start();
        }

        /// <summary>
        /// 下载停止
        /// </summary>
        public void Stop()
        {
            Stopped = true;
        }

        /// <summary>
        /// 暂停等待下载
        /// </summary>
        public void Wait()
        {
            _wait = true;
        }

        /// <summary>
        /// 稍后唤醒
        /// </summary>
        public void ResumeAfterWait()
        {
            _wait = false;
        }
    }
}