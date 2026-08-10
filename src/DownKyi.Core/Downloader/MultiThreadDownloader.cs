using DownKyi.Core.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Core.Downloader
{
    /// <summary>
    /// 文件合并改变事件
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    public delegate void FileMergeProgressChangedEventHandler(object sender, int e);

    /// <summary>
    /// 多线程下载器
    /// </summary>
    public class MultiThreadDownloader
    {
        #region 属性

        private string _url;
        private bool _rangeAllowed;
        private readonly HttpWebRequest _request;
        private Action<HttpWebRequest> _requestConfigure = req => req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.122 Safari/537.36";

        /// <summary>
        /// 合并状态：0 = 未开始，1 = 已开始。
        /// 用 Interlocked 操作，保证 MergeParts 在整个生命周期内有且仅执行一次。
        /// </summary>
        private int _mergeState;

        /// <summary>
        /// 合并分片时打开文件的最大重试次数
        /// </summary>
        private const int MergeRetryCount = 6;

        /// <summary>
        /// 合并分片重试的基础退避间隔（毫秒），实际间隔为 基础间隔 × 重试轮次
        /// </summary>
        private const int MergeRetryBaseDelayMs = 150;

        #endregion 属性

        #region 公共属性

        /// <summary>
        /// RangeAllowed
        /// </summary>
        public bool RangeAllowed
        {
            get => _rangeAllowed;
            set => _rangeAllowed = value;
        }

        /// <summary>
        /// 临时文件夹
        /// </summary>
        public string TempFileDirectory { get; set; }

        /// <summary>
        /// url地址
        /// </summary>
        public string Url
        {
            get => _url;
            set => _url = value;
        }

        /// <summary>
        /// 第几部分
        /// </summary>
        public int NumberOfParts { get; set; }

        /// <summary>
        /// 已接收字节数
        /// </summary>
        public long TotalBytesReceived
        {
            get
            {
                try
                {
                    lock (this)
                    {
                        return PartialDownloaderList.Where(t => t != null).Sum(t => t.TotalBytesRead);
                    }
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// 总进度
        /// </summary>
        public float TotalProgress { get; private set; }

        /// <summary>
        /// 文件大小
        /// </summary>
        public long Size { get; private set; }

        /// <summary>
        /// 下载速度
        /// </summary>
        public float TotalSpeedInBytes
        {
            get
            {
                lock (this)
                {
                    return PartialDownloaderList.Sum(t => t.SpeedInBytes);
                }
            }
        }

        /// <summary>
        /// 下载块
        /// </summary>
        public List<PartialDownloader> PartialDownloaderList { get; }

        /// <summary>
        /// 文件路径
        /// </summary>
        public string FilePath { get; set; }

        #endregion 公共属性

        #region 变量

        /// <summary>
        /// 总下载进度更新事件
        /// </summary>
        public event EventHandler TotalProgressChanged;

        /// <summary>
        /// 文件合并完成事件
        /// </summary>
        public event EventHandler FileMergedComplete;

        /// <summary>
        /// 文件合并事件
        /// </summary>
        public event FileMergeProgressChangedEventHandler FileMergeProgressChanged;

        /// <summary>
        /// 下载或合并失败事件。
        /// 下载相关逻辑全部运行在后台线程/线程池上，异常一旦逃逸就会终止进程，
        /// 因此统一收敛为该事件，交由上层决定重试还是标记任务失败。
        /// </summary>
        public event EventHandler<DownloadErrorEventArgs> DownloadError;

        private readonly AsyncOperation _aop;

        #endregion 变量

        /// <summary>
        /// 最后一次失败的异常，null 表示尚未出错
        /// </summary>
        public Exception LastError { get; private set; }

        #region 下载管理器

        /// <summary>
        /// 多线程下载管理器
        /// </summary>
        /// <param name="sourceUrl"></param>
        /// <param name="tempDir"></param>
        /// <param name="savePath"></param>
        /// <param name="numOfParts"></param>
        public MultiThreadDownloader(string sourceUrl, string tempDir, string savePath, int numOfParts)
        {
            _url = sourceUrl;
            NumberOfParts = numOfParts;
            TempFileDirectory = tempDir;
            PartialDownloaderList = new List<PartialDownloader>();
            _aop = AsyncOperationManager.CreateOperation(null);
            FilePath = savePath;
            _request = WebRequest.Create(sourceUrl) as HttpWebRequest;
        }

        /// <summary>
        /// 多线程下载管理器
        /// </summary>
        /// <param name="sourceUrl"></param>
        /// <param name="savePath"></param>
        /// <param name="numOfParts"></param>
        public MultiThreadDownloader(string sourceUrl, string savePath, int numOfParts) : this(sourceUrl, null, savePath, numOfParts)
        {
            TempFileDirectory = Environment.GetEnvironmentVariable("temp");
        }

        /// <summary>
        /// 多线程下载管理器
        /// </summary>
        /// <param name="sourceUrl"></param>
        /// <param name="numOfParts"></param>
        public MultiThreadDownloader(string sourceUrl, int numOfParts) : this(sourceUrl, null, numOfParts)
        {
        }

        #endregion 下载管理器

        #region 事件

        private void temp_DownloadPartCompleted(object sender, EventArgs e)
        {
            // 本方法由 AsyncOperation.Post 派发，通常运行在线程池线程上。
            // 线程池线程上的未处理异常会直接终止进程，所以整体包一层兜底。
            try
            {
                PartialDownloader newPart = null;

                lock (this)
                {
                    WaitOrResumeAll(PartialDownloaderList, true);

                    if (TotalBytesReceived == Size)
                    {
                        UpdateProgress();

                        // 关键修复：
                        // 多个分片可能几乎同时完成，于是多个线程同时满足
                        // TotalBytesReceived == Size，并发进入 MergeParts，
                        // 同时以写方式打开同一个目标文件 -> IOException -> 进程崩溃。
                        // 这里用 Interlocked 抢占，确保合并全局只执行一次。
                        if (Interlocked.CompareExchange(ref _mergeState, 1, 0) != 0)
                        {
                            return;
                        }
                    }
                    else
                    {
                        if (PartialDownloaderList.Count == 0)
                        {
                            return;
                        }

                        PartialDownloaderList.Sort((x, y) => (int)(y.RemainingBytes - x.RemainingBytes));
                        var rem = PartialDownloaderList[0].RemainingBytes;
                        if (rem < 50 * 1024)
                        {
                            WaitOrResumeAll(PartialDownloaderList, false);
                            return;
                        }

                        var from = PartialDownloaderList[0].CurrentPosition + rem / 2;
                        var to = PartialDownloaderList[0].To;
                        if (from > to)
                        {
                            WaitOrResumeAll(PartialDownloaderList, false);
                            return;
                        }

                        PartialDownloaderList[0].To = from - 1;
                        WaitOrResumeAll(PartialDownloaderList, false);

                        newPart = new PartialDownloader(_url, TempFileDirectory, Guid.NewGuid().ToString("N"), from, to, true);
                        newPart.DownloadPartCompleted += temp_DownloadPartCompleted;
                        newPart.DownloadPartProgressChanged += temp_DownloadPartProgressChanged;
                        newPart.DownloadPartFailed += temp_DownloadPartFailed;
                        PartialDownloaderList.Add(newPart);
                    }
                }

                // 启动新分片和合并都放在锁外执行，避免长时间持锁造成阻塞
                if (newPart != null)
                {
                    newPart.Start(_requestConfigure);
                    return;
                }

                MergeParts();
            }
            catch (Exception ex)
            {
                HandleError("temp_DownloadPartCompleted", ex);
            }
        }

        private void temp_DownloadPartProgressChanged(object sender, EventArgs e)
        {
            try
            {
                UpdateProgress();
            }
            catch (Exception ex)
            {
                HandleError("temp_DownloadPartProgressChanged", ex);
            }
        }

        private void temp_DownloadPartFailed(object sender, DownloadErrorEventArgs e)
        {
            // 任意一个分片失败都意味着整个文件无法拼装完整，
            // 直接上报错误，避免上层在等待循环里无限阻塞。
            HandleError("PartialDownloader", e.Exception);
        }

        private void UpdateProgress()
        {
            // Size 为 0 时（例如服务端未返回 Content-Length）会算出 Infinity，
            // 强转 int 得到脏数据，这里直接跳过
            if (Size <= 0)
            {
                return;
            }

            int pr = (int)(TotalBytesReceived * 1d / Size * 100);
            if (TotalProgress != pr)
            {
                TotalProgress = pr;

                var handler = TotalProgressChanged;
                if (handler != null)
                {
                    _aop.Post(state => handler(this, EventArgs.Empty), null);
                }
            }
        }

        /// <summary>
        /// 统一的错误上报入口：记录日志并通知上层，绝不让异常继续向上抛
        /// </summary>
        /// <param name="source"></param>
        /// <param name="ex"></param>
        private void HandleError(string source, Exception ex)
        {
            LastError = ex;

            try
            {
                LogManager.Error($"MultiThreadDownloader.{source}()", ex);
            }
            catch
            {
                // 日志本身失败时不能再抛异常
            }

            try
            {
                var handler = DownloadError;
                if (handler != null)
                {
                    _aop.Post(state => handler(this, new DownloadErrorEventArgs(ex)), null);
                }
            }
            catch
            {
                // ignored
            }
        }

        #endregion 事件

        #region 方法

        private void CreateFirstPartitions()
        {
            // 该方法在独立 Task 中执行，异常逃逸会变成未观察到的任务异常，
            // 结果是下载既不完成也不报错，上层等待循环永久阻塞。
            try
            {
                Size = GetContentLength(ref _rangeAllowed, ref _url);

                if (Size <= 0)
                {
                    throw new IOException($"无法获取文件大小，服务端未返回有效的 Content-Length: {_url}");
                }

                if (!string.IsNullOrEmpty(TempFileDirectory))
                {
                    Directory.CreateDirectory(TempFileDirectory);
                }

                int maximumPart = (int)(Size / (25 * 1024));
                maximumPart = maximumPart == 0 ? 1 : maximumPart;
                if (!_rangeAllowed)
                {
                    NumberOfParts = 1;
                }
                else if (NumberOfParts > maximumPart)
                {
                    NumberOfParts = maximumPart;
                }

                if (NumberOfParts < 1)
                {
                    NumberOfParts = 1;
                }

                for (int i = 0; i < NumberOfParts; i++)
                {
                    var temp = CreateNew(i, NumberOfParts, Size);
                    temp.DownloadPartProgressChanged += temp_DownloadPartProgressChanged;
                    temp.DownloadPartCompleted += temp_DownloadPartCompleted;
                    temp.DownloadPartFailed += temp_DownloadPartFailed;
                    lock (this)
                    {
                        PartialDownloaderList.Add(temp);
                    }
                    temp.Start(_requestConfigure);
                }
            }
            catch (Exception ex)
            {
                HandleError("CreateFirstPartitions", ex);
            }
        }

        private void MergeParts()
        {
            try
            {
                MergePartsCore();
            }
            catch (Exception ex)
            {
                // 合并失败时允许后续重新触发合并（例如上层重试下载）
                Interlocked.Exchange(ref _mergeState, 0);
                HandleError("MergeParts", ex);
                return;
            }

            var handler = FileMergedComplete;
            if (handler != null)
            {
                _aop.Post(state => handler(state, EventArgs.Empty), this);
            }
        }

        /// <summary>
        /// 实际的分片合并逻辑
        /// </summary>
        private void MergePartsCore()
        {
            // 原实现直接对 PartialDownloaderList 做 OrderBy 惰性枚举，
            // 而其他线程仍可能向该列表 Add，触发
            // InvalidOperationException: Collection was modified。
            // 这里在锁内先做快照，再在锁外安全遍历。
            List<PartialDownloader> mergeOrderedList;
            lock (this)
            {
                mergeOrderedList = PartialDownloaderList
                    .Where(x => x != null)
                    .OrderBy(x => x.From)
                    .ToList();
            }

            if (mergeOrderedList.Count == 0)
            {
                throw new IOException("没有可供合并的分片文件");
            }

            // FilePath 可能是相对路径，DirectoryName 也可能为 null，
            // 原实现会在这种情况下抛 ArgumentNullException。
            var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            long totalBytesWrite = 0;
            int mergeProgress = 0;

            // 使用 FileMode.Create 而非 File.OpenWrite：
            // OpenWrite 不会截断已存在的文件，若目标文件残留了更大的旧内容，
            // 合并后会在尾部留下垃圾数据导致视频损坏。
            using (var fs = OpenWithRetry(
                () => new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None),
                FilePath))
            {
                foreach (var item in mergeOrderedList)
                {
                    // 允许 ReadWrite 共享并配合退避重试：
                    // 即使某个分片的写句柄尚未被操作系统完全回收，也能正常读取，
                    // 彻底消除合并阶段的文件句柄冲突。
                    using (var pdi = OpenWithRetry(
                        () => new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                        item.FullPath))
                    {
                        byte[] buffer = new byte[81920];
                        int read;
                        while ((read = pdi.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            fs.Write(buffer, 0, read);
                            totalBytesWrite += read;

                            if (Size <= 0)
                            {
                                continue;
                            }

                            int temp = (int)(totalBytesWrite * 1d / Size * 100);
                            var progressHandler = FileMergeProgressChanged;
                            if (temp != mergeProgress && progressHandler != null)
                            {
                                mergeProgress = temp;
                                _aop.Post(state => progressHandler(this, temp), null);
                            }
                        }
                    }

                    TryDeletePart(item.FullPath);
                }

                fs.Flush();
            }
        }

        /// <summary>
        /// 带退避重试地打开文件流。
        /// 合并阶段最常见的失败就是瞬时的文件句柄冲突（IOException），
        /// 短暂等待后重试即可成功，不应该让整个进程崩溃。
        /// </summary>
        /// <param name="factory"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        private static FileStream OpenWithRetry(Func<FileStream> factory, string path)
        {
            Exception lastError = null;

            for (int attempt = 0; attempt < MergeRetryCount; attempt++)
            {
                try
                {
                    return factory();
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = ex;
                }

                Thread.Sleep(MergeRetryBaseDelayMs * (attempt + 1));
            }

            throw new IOException($"合并分片时无法打开文件（已重试 {MergeRetryCount} 次）: {path}", lastError);
        }

        /// <summary>
        /// 尽力删除临时分片文件，失败不影响合并结果
        /// </summary>
        /// <param name="path"></param>
        private static void TryDeletePart(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return;
                }
                catch (IOException)
                {
                    // 句柄尚未释放，稍后重试
                }
                catch (UnauthorizedAccessException)
                {
                    // 只读或权限问题，稍后重试
                }

                Thread.Sleep(100);
            }
        }

        private PartialDownloader CreateNew(int order, int parts, long contentLength)
        {
            var division = contentLength / parts;
            var remaining = contentLength % parts;
            var start = division * order;
            var end = start + division - 1;
            end += order == parts - 1 ? remaining : 0;
            return new PartialDownloader(_url, TempFileDirectory, Guid.NewGuid().ToString("N"), start, end, true);
        }

        /// <summary>
        /// 暂停或继续
        /// </summary>
        /// <param name="list"></param>
        /// <param name="wait"></param>
        public static void WaitOrResumeAll(List<PartialDownloader> list, bool wait)
        {
            for (var index = 0; index < list.Count; index++)
            {
                if (wait)
                {
                    list[index].Wait();
                }
                else
                {
                    list[index].ResumeAfterWait();
                }
            }
        }

        /// <summary>
        /// 配置请求头
        /// </summary>
        /// <param name="config"></param>
        public void Configure(Action<HttpWebRequest> config)
        {
            _requestConfigure = config;
        }

        /// <summary>
        /// 获取内容长度
        /// </summary>
        /// <param name="rangeAllowed"></param>
        /// <param name="redirectedUrl"></param>
        /// <returns></returns>
        public long GetContentLength(ref bool rangeAllowed, ref string redirectedUrl)
        {
            _request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.122 Safari/537.36";
            _request.ServicePoint.ConnectionLimit = 4;
            _requestConfigure(_request);

            using (var resp = _request.GetResponse() as HttpWebResponse)
            {
                redirectedUrl = resp.ResponseUri.OriginalString;
                var ctl = resp.ContentLength;
                rangeAllowed = resp.Headers.AllKeys.Select((v, i) => new
                {
                    HeaderName = v,
                    HeaderValue = resp.Headers[i]
                }).Any(k => k.HeaderName.ToLower().Contains("range") && k.HeaderValue.ToLower().Contains("byte"));
                _request.Abort();
                return ctl;
            }
        }

        #endregion 方法

        #region 公共方法

        /// <summary>
        /// 暂停下载
        /// </summary>
        public void Pause()
        {
            lock (this)
            {
                foreach (var t in PartialDownloaderList.Where(t => !t.Completed))
                {
                    t.Stop();
                }
            }

            Thread.Sleep(200);
        }

        /// <summary>
        /// 开始下载
        /// </summary>
        public void Start()
        {
            Task th = new Task(CreateFirstPartitions);
            th.Start();
        }

        /// <summary>
        /// 唤醒下载
        /// </summary>
        public void Resume()
        {
            var pendingStart = new List<PartialDownloader>();

            lock (this)
            {
                int count = PartialDownloaderList.Count;
                for (int i = 0; i < count; i++)
                {
                    if (!PartialDownloaderList[i].Stopped)
                    {
                        continue;
                    }

                    var from = PartialDownloaderList[i].CurrentPosition + 1;
                    var to = PartialDownloaderList[i].To;
                    if (from > to)
                    {
                        continue;
                    }

                    var temp = new PartialDownloader(_url, TempFileDirectory, Guid.NewGuid().ToString("N"), from, to, _rangeAllowed);
                    temp.DownloadPartProgressChanged += temp_DownloadPartProgressChanged;
                    temp.DownloadPartCompleted += temp_DownloadPartCompleted;
                    temp.DownloadPartFailed += temp_DownloadPartFailed;

                    PartialDownloaderList[i].To = PartialDownloaderList[i].CurrentPosition;
                    PartialDownloaderList.Add(temp);
                    pendingStart.Add(temp);
                }
            }

            // 在锁外启动，避免持锁期间创建线程
            foreach (var item in pendingStart)
            {
                item.Start(_requestConfigure);
            }
        }

        #endregion 公共方法
    }
}
