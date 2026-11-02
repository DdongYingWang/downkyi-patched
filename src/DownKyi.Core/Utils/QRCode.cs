using System.Drawing;

namespace DownKyi.Core.Utils
{
    public static class QRCode
    {
        /// <summary>
        /// 生成二维码
        /// </summary>
        /// <param name="msg">信息</param>
        /// <param name="version">版本 1 ~ 40</param>
        /// <param name="pixel">像素点大小</param>
        /// <param name="icon_path">图标路径</param>
        /// <param name="icon_size">图标尺寸</param>
        /// <param name="icon_border">图标边框厚度</param>
        /// <param name="white_edge">二维码白边</param>
        /// <returns>位图</returns>
        public static Bitmap EncodeQRCode(string msg, int version, int pixel, string icon_path, int icon_size, int icon_border, bool white_edge)
        {
            QRCoder.QRCodeGenerator code_generator = new QRCoder.QRCodeGenerator();

            // version <= 0 视为“自动选版”：QRCoder 约定请求版本 -1 代表自动，
            // 否则会拿非法版本号去索引版本表导致“索引超出范围”。1~40 为固定版本。
            int qrVersion = version < 1 ? -1 : version;
            QRCoder.QRCodeData code_data = code_generator.CreateQrCode(msg, QRCoder.QRCodeGenerator.ECCLevel.H/* 这里设置容错率的一个级别 */, true, false, QRCoder.QRCodeGenerator.EciMode.Utf8, qrVersion);

            QRCoder.QRCode code = new QRCoder.QRCode(code_data);

            Bitmap icon;
            if (icon_path == null || icon_path == "")
            {
                icon = null;
            }
            else
            {
                icon = new Bitmap(icon_path);
            }

            Bitmap bmp = code.GetGraphic(pixel, Color.Black, Color.White, icon, icon_size, icon_border, white_edge);
            return bmp;
        }

    }
}
