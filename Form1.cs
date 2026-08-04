using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;

namespace RegionScreenRecorder
{
    public partial class MainForm : Form
    {
        #region Win32 API 截屏
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
            IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        private const int SRCCOPY = 0x00CC0020;
        #endregion

        private Rectangle _recordRegion;        // 录制区域
        private bool _isRecording = false;
        private Process _ffmpegProcess;
        private Thread _captureThread;
        private readonly int _fps = 24;         // 帧率
        private string _videoSavePath;

        public MainForm()
        {
            InitializeComponent();
            btnStop.Enabled = false;
        }

        // 鼠标框选区域按钮
        private void btnSelectRegion_Click(object sender, EventArgs e)
        {
            Hide();
            using (var selector = new RegionSelectForm())
            {
                if (selector.ShowDialog() == DialogResult.OK)
                {
                    _recordRegion = selector.SelectedRegion;
                    lblRegionInfo.Text = $"录制区域：X:{_recordRegion.X} Y:{_recordRegion.Y} W:{_recordRegion.Width} H:{_recordRegion.Height}";
                }
            }
            Show();
        }

        // 开始录制
        private void btnStart_Click(object sender, EventArgs e)
        {
            //if (_recordRegion.Width <= 0 || _recordRegion.Height <= 0)
            //{
            //    MessageBox.Show("请先框选录制区域！");
            //    return;
            //}

            //int w = _recordRegion.Width & ~1;
            //int h = _recordRegion.Height & ~1;
            //_recordRegion = new Rectangle(_recordRegion.X, _recordRegion.Y, w, h);

            //if (w < 16 || h < 16)
            //{
            //    MessageBox.Show("录制区域尺寸过小，请重新选择区域！");
            //    return;
            //}
            if (_recordRegion.Width <= 0 || _recordRegion.Height <= 0)
            {
                MessageBox.Show("请先框选录制区域！");
                return;
            }
            // 强制宽高变成偶数
            int w = _recordRegion.Width;
            int h = _recordRegion.Height;
            w = w - (w % 2);
            h = h - (h % 2);
            if (w < 32 || h < 32)
            {
                MessageBox.Show("选区过小！");
                return;
            }
            _recordRegion = new Rectangle(_recordRegion.X, _recordRegion.Y, w, h);

            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "MP4视频(*.mp4)|*.mp4",
                FileName = $"录屏_{DateTime.Now:yyyyMMdd_HHmmss}.mp4"
            };
            if (sfd.ShowDialog() != DialogResult.OK) return;
            _videoSavePath = sfd.FileName;

            StartFFmpeg();
            _isRecording = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true };
            _captureThread.Start();
            
            btnStart.Enabled = false;
            btnSelectRegion.Enabled = false;
            btnStop.Enabled = true;
        }

        // 停止录制
        private void btnStop_Click(object sender, EventArgs e)
        {
            if (!_isRecording) return;
            _isRecording = false;
            Thread.Sleep(200);

            try
            {
                _ffmpegProcess?.StandardInput.Close();
                _ffmpegProcess?.StandardInput.BaseStream?.Dispose();

                // 新开后台线程等待ffmpeg，防止UI卡死
                new Thread(() =>
                {
                    bool exit = _ffmpegProcess.WaitForExit(10000);
                    if (!exit)
                    {
                        _ffmpegProcess.Kill();
                        MessageBox.Show("录制超时，强制终止，视频可能损坏！");
                    }

                    this.Invoke(new Action(() =>
                    {
                        _ffmpegProcess?.Dispose();
                        _ffmpegProcess = null;
                        btnStart.Enabled = true;
                        btnSelectRegion.Enabled = true;
                        btnStop.Enabled = false;
                        MessageBox.Show($"录制完成！\n{_videoSavePath}");
                    }));
                })
                { IsBackground = true }.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止异常：{ex.Message}");
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
                btnStart.Enabled = true;
                btnSelectRegion.Enabled = true;
                btnStop.Enabled = false;
            }
        }
        // 在类内部添加
        private Task _stderrReadTask;
        // 启动FFmpeg管道接收图片流
        void StartFFmpeg()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = @"ffmpeg\ffmpeg.exe",
                Arguments =
                    $"-y -thread_queue_size 1024 " +
                    $"-f rawvideo -pix_fmt bgr24 -video_size {_recordRegion.Width}x{_recordRegion.Height} -framerate {_fps} -i pipe:0 " +
                    $"-c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -movflags +faststart \"{_videoSavePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            _ffmpegProcess = Process.Start(startInfo);

            // 单独线程消费stderr，防止缓冲区阻塞
            new Thread(() =>
            {
                while (!_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.StandardError.ReadLine();
                }
            })
            { IsBackground = true }.Start();
        }

        // 循环截屏写入FFmpeg标准输入
        //void CaptureLoop()
        //{
        //    long frameDelay = (long)(1000.0 / _fps);
        //    while (_isRecording)
        //    {
        //        var bitmap = CaptureRegion(_recordRegion);
        //        if (bitmap == null) continue;

        //        // BGR24 原始像素数据
        //        var bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
        //            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        //        int byteCount = bmpData.Stride * bitmap.Height;
        //        byte[] buffer = new byte[byteCount];
        //        System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, buffer, 0, byteCount);
        //        bitmap.UnlockBits(bmpData);

        //        try
        //        {
        //            _ffmpegProcess.StandardInput.BaseStream.Write(buffer, 0, buffer.Length);
        //            _ffmpegProcess.StandardInput.BaseStream.Flush();
        //        }
        //        catch
        //        {
        //            break;
        //        }

        //        bitmap.Dispose();
        //        Thread.Sleep((int)frameDelay);
        //    }
        //}
        void CaptureLoop()
        {
            long frameDelay = (long)(1000.0 / _fps);
            int width = _recordRegion.Width;
            int height = _recordRegion.Height;
            int cleanLineBytes = width * 3; // ffmpeg严格要求：每行像素字节，不含填充

            while (_isRecording)
            {
                Bitmap bitmap = CaptureRegion(_recordRegion);
                if (bitmap == null)
                {
                    Thread.Sleep((int)frameDelay);
                    continue;
                }

                Rectangle bmpRect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                BitmapData bmpData = bitmap.LockBits(bmpRect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

                byte[] frameBuffer = new byte[width * height * 3];
                unsafe
                {
                    byte* srcPtr = (byte*)bmpData.Scan0;
                    fixed (byte* dstPtr = frameBuffer)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            // 只复制有效像素，跳过每行末尾系统填充字节
                            Buffer.MemoryCopy(
                                srcPtr + y * bmpData.Stride,
                                dstPtr + y * cleanLineBytes,
                                cleanLineBytes,
                                cleanLineBytes);
                        }
                    }
                }

                bitmap.UnlockBits(bmpData);

                try
                {
                    if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
                        break;
                    _ffmpegProcess.StandardInput.BaseStream.Write(frameBuffer, 0, frameBuffer.Length);
                }
                catch (IOException)
                {
                    break;
                }

                bitmap.Dispose();
                Thread.Sleep((int)frameDelay);
            }
        }
        /// <summary>
        /// Win32 API 截取指定屏幕区域
        /// </summary>
        private Bitmap CaptureRegion(Rectangle rect)
        {
            IntPtr hScreenDC = GetDC(IntPtr.Zero);
            IntPtr hMemDC = CreateCompatibleDC(hScreenDC);
            IntPtr hBitmap = CreateCompatibleBitmap(hScreenDC, rect.Width, rect.Height);
            IntPtr hOldBmp = SelectObject(hMemDC, hBitmap);

            BitBlt(hMemDC, 0, 0, rect.Width, rect.Height, hScreenDC, rect.X, rect.Y, SRCCOPY);
            Bitmap bmp = Bitmap.FromHbitmap(hBitmap);

            // 释放GDI资源（极其重要，防止内存泄漏）
            SelectObject(hMemDC, hOldBmp);
            DeleteObject(hBitmap);
            DeleteObject(hOldBmp);
            ReleaseDC(IntPtr.Zero, hScreenDC);
            ReleaseDC(IntPtr.Zero, hMemDC);
            return bmp;
        }
    }
}