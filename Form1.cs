using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.Text;

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

        private Rectangle _recordRegion;
        private bool _isRecording = false;
        private Process _ffmpegProcess;
        private Thread _captureThread;
        private readonly int _fps = 24;
        private string _videoSavePath;

        // 音频控件
        private GroupBox grpAudio;
        private CheckBox chkRecordAudio;
        private ComboBox cmbAudioDevices;
        private Button btnRefreshDevices;
        private Label lblAudioStatus;

        public MainForm()
        {
            InitializeComponent();
            btnStop.Enabled = false;
            InitializeAudioControls();
        }

        private void InitializeAudioControls()
        {
            grpAudio = new GroupBox
            {
                Text = "音频录制设置",
                Location = new Point(12, 180),
                Size = new Size(460, 110)
            };

            chkRecordAudio = new CheckBox
            {
                Text = "同时录制麦克风/音频",
                AutoSize = true,
                Location = new Point(10, 20),
                Checked = false
            };

            cmbAudioDevices = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 320,
                Location = new Point(10, 50),
                Enabled = false
            };

            btnRefreshDevices = new Button
            {
                Text = "刷新设备",
                Location = new Point(340, 49),
                Size = new Size(75, 23),
                Enabled = false
            };

            lblAudioStatus = new Label
            {
                Text = "",
                AutoSize = true,
                Location = new Point(10, 80),
                ForeColor = Color.Gray
            };

            chkRecordAudio.CheckedChanged += (s, e) =>
            {
                cmbAudioDevices.Enabled = chkRecordAudio.Checked;
                btnRefreshDevices.Enabled = chkRecordAudio.Checked;
            };

            btnRefreshDevices.Click += (s, e) => LoadAudioDevices();

            grpAudio.Controls.Add(chkRecordAudio);
            grpAudio.Controls.Add(cmbAudioDevices);
            grpAudio.Controls.Add(btnRefreshDevices);
            grpAudio.Controls.Add(lblAudioStatus);
            this.Controls.Add(grpAudio);

            this.Load += (s, e) => LoadAudioDevices();
        }

        // ========== 临时固定中文设备名，跳过扫描乱码问题 ==========
        private void LoadAudioDevices()
        {
            cmbAudioDevices.Items.Clear();

            // 【手动填写你的设备名】
            cmbAudioDevices.Items.Add("麦克风 (Realtek(R) Audio)");
            // 如需录系统声音，取消下面注释：
            // cmbAudioDevices.Items.Add("立体声混音 (Realtek(R) Audio)");

            cmbAudioDevices.SelectedIndex = 0;
            lblAudioStatus.Text = "已加载预设设备（手动模式）";
            lblAudioStatus.ForeColor = Color.Green;
        }

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

        private void btnStart_Click(object sender, EventArgs e)
        {
            if (_recordRegion.Width <= 0 || _recordRegion.Height <= 0)
            {
                MessageBox.Show("请先框选录制区域！");
                return;
            }

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

        // ========== 核心修复：停止逻辑 ==========
        private void btnStop_Click(object sender, EventArgs e)
        {
            if (!_isRecording) return;
            _isRecording = false;  // 先通知捕获线程停止

            // 等捕获线程结束（最多3秒），确保不再往 pipe 写数据
            if (_captureThread != null && _captureThread.IsAlive)
                _captureThread.Join(3000);

            // 后台线程处理 FFmpeg 退出，防止 UI 卡死
            new Thread(() =>
            {
                try
                {
                    // 关闭视频输入管道，FFmpeg 收到 EOF
                    _ffmpegProcess?.StandardInput.Close();
                }
                catch { }

                // 因为有 -shortest，视频 EOF 后 FFmpeg 会自动停止音频并退出
                bool exit = _ffmpegProcess.WaitForExit(8000);

                if (!exit)
                {
                    try { _ffmpegProcess.Kill(); } catch { }
                    this.Invoke(new Action(() =>
                    {
                        MessageBox.Show("录制超时，强制终止，视频可能损坏！");
                    }));
                }

                this.Invoke(new Action(() =>
                {
                    _ffmpegProcess?.Dispose();
                    _ffmpegProcess = null;
                    btnStart.Enabled = true;
                    btnSelectRegion.Enabled = true;
                    btnStop.Enabled = false;

                    if (exit)
                        MessageBox.Show($"录制完成！\n{_videoSavePath}");
                }));
            })
            { IsBackground = true }.Start();
        }

        // ========== 核心修复：FFmpeg 参数加 -shortest ==========
        void StartFFmpeg()
        {
            bool recordAudio = chkRecordAudio.Checked
                && cmbAudioDevices.SelectedItem != null;

            // 视频加 -use_wallclock_as_timestamps 1，让时间戳基于真实写入时间
            string args = $"-y -fflags +genpts " +
                $"-f rawvideo -pix_fmt bgr24 -video_size {_recordRegion.Width}x{_recordRegion.Height} -framerate {_fps} -use_wallclock_as_timestamps 1 -thread_queue_size 4096 -i pipe:0 ";

            if (recordAudio)
            {
                string deviceName = cmbAudioDevices.SelectedItem.ToString();
                args += $"-f dshow -audio_buffer_size 100 -thread_queue_size 4096 -i audio=\"{deviceName}\" " +
                        $"-c:a aac -b:a 128k -ar 44100 ";
            }

            args += $"-c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -vsync cfr -movflags +faststart -shortest \"{_videoSavePath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = @"ffmpeg\ffmpeg.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            _ffmpegProcess = Process.Start(startInfo);

            new Thread(() =>
            {
                while (!_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.StandardError.ReadLine();
                }
            })
            { IsBackground = true }.Start();
        }

        // ========== 核心修复：精确帧率控制 ==========
        void CaptureLoop()
        {
            var sw = Stopwatch.StartNew();
            long frameInterval = (long)(1000.0 / _fps);  // 24fps = 41.6ms
            int frameIndex = 0;
            int width = _recordRegion.Width;
            int height = _recordRegion.Height;
            int cleanLineBytes = width * 3;

            while (_isRecording)
            {
                // 计算下一帧的目标时间点，精确等待
                long targetTime = frameIndex * frameInterval;
                long sleepMs = targetTime - sw.ElapsedMilliseconds;
                if (sleepMs > 0)
                    Thread.Sleep((int)sleepMs);

                Bitmap bitmap = CaptureRegion(_recordRegion);
                if (bitmap == null)
                {
                    frameIndex++;  // 即使截图失败也推进时间，避免累积延迟
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
                frameIndex++;
            }
        }

        private Bitmap CaptureRegion(Rectangle rect)
        {
            IntPtr hScreenDC = GetDC(IntPtr.Zero);
            IntPtr hMemDC = CreateCompatibleDC(hScreenDC);
            IntPtr hBitmap = CreateCompatibleBitmap(hScreenDC, rect.Width, rect.Height);
            IntPtr hOldBmp = SelectObject(hMemDC, hBitmap);

            BitBlt(hMemDC, 0, 0, rect.Width, rect.Height, hScreenDC, rect.X, rect.Y, SRCCOPY);
            Bitmap bmp = Bitmap.FromHbitmap(hBitmap);

            SelectObject(hMemDC, hOldBmp);
            DeleteObject(hBitmap);
            DeleteObject(hOldBmp);
            ReleaseDC(IntPtr.Zero, hScreenDC);
            ReleaseDC(IntPtr.Zero, hMemDC);
            return bmp;
        }
    }
}