using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Management;
using System.Windows.Forms;
using System.Diagnostics;
using System.Text.RegularExpressions;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace TiKey
{
    public partial class MainForm : Form
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const string USB_INTERFACE_TYPE = "USB";
        private const string VERACRYPT_HIDDEN_FOLDER = ".DO_NOT_DELETE";
        private const string AUTORUN_FOLDER = "AUTORUN.INF";
        private const string DEFAULT_PASSWORD = "111";

        public MainForm()
        {
            InitializeComponent();
            LoadUSBDevices();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_DEVICECHANGE)
            {
                int wParam = m.WParam.ToInt32();
                if (wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE)
                {
                    // Sử dụng BeginInvoke để tránh xung đột luồng
                    BeginInvoke(new Action(LoadUSBDevices));
                }
            }
        }

        private void LoadUSBDevices()
        {
            LogMessage("Đang tải danh sách thiết bị USB...");
            cbbUSB.Items.Clear();
            var usbDevices = new HashSet<string>(); // Sử dụng HashSet để tránh trùng lặp

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_DiskDrive WHERE InterfaceType='" + USB_INTERFACE_TYPE + "'"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string name = device["Model"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            usbDevices.Add(name);
                        }
                    }
                }

                // Thêm các thiết bị vào ComboBox
                foreach (var device in usbDevices)
                {
                    cbbUSB.Items.Add(device);
                }

                // Cập nhật chọn lựa
                cbbUSB.SelectedIndex = cbbUSB.Items.Count > 0 ? 0 : -1;
                cbbUSB.Text = cbbUSB.SelectedIndex == -1 ? "" : cbbUSB.SelectedItem.ToString();

                LogMessage($"Đã tìm thấy {usbDevices.Count} thiết bị USB.");
            }
            catch (Exception ex)
            {
                LogMessage($"Lỗi khi tải danh sách USB: {ex.Message}", true);
                MessageBox.Show($"Lỗi khi tải danh sách USB: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnReload_Click(object sender, EventArgs e)
        {
            LoadUSBDevices();
        }

        private int ExtractDiskNumber(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return -1;

            var match = Regex.Match(deviceId, @"PHYSICALDRIVE(\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : -1;
        }

        private List<string> GetUSBDriveLetters()
        {
            var result = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_LogicalDisk WHERE DriveType = 2"))
                {
                    foreach (ManagementObject disk in searcher.Get())
                    {
                        string driveLetter = disk["DeviceID"]?.ToString();
                        if (!string.IsNullOrEmpty(driveLetter))
                        {
                            result.Add(driveLetter);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Lỗi khi tìm ký tự ổ đĩa USB: {ex.Message}", true);
                MessageBox.Show($"Lỗi khi tìm ký tự ổ đĩa USB: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return result;
        }

        private async Task<List<string>> WaitForUSBDriveLetters(int maxAttempts = 5)
        {
            LogMessage("Đang chờ hệ thống nhận diện USB...");
            List<string> usbDrives = new List<string>();
            int attempts = 0;

            while (usbDrives.Count == 0 && attempts < maxAttempts)
            {
                usbDrives = GetUSBDriveLetters();
                if (usbDrives.Count == 0)
                {
                    // Chờ một khoảng thời gian trước khi thử lại
                    LogMessage($"Đang thử lại ({attempts + 1}/{maxAttempts})...");
                    await Task.Delay(1000);
                    attempts++;
                }
            }

            if (usbDrives.Count > 0)
            {
                LogMessage($"Đã tìm thấy ổ USB: {string.Join(", ", usbDrives)}");
            }
            else
            {
                LogMessage("Không tìm thấy ổ USB sau nhiều lần thử.", true);
            }

            return usbDrives;
        }

        private async Task<bool> CreateVeraCryptContainer(string driveLetter)
        {
            try
            {
                LogMessage("Đang kiểm tra ổ đĩa...");
                UpdateProgress(60);

                // Kiểm tra ổ đĩa
                DriveInfo drive = new DriveInfo(driveLetter);
                if (!drive.IsReady)
                {
                    LogMessage($"Ổ đĩa {driveLetter} chưa sẵn sàng.", true);
                    MessageBox.Show($"Ổ đĩa {driveLetter} chưa sẵn sàng.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }

                // Đường dẫn đến VeraCryptFormat.exe nằm trên USB
                string hiddenFolder = Path.Combine(driveLetter, VERACRYPT_HIDDEN_FOLDER);
                string veraCryptExe = Path.Combine(hiddenFolder, "VeraCryptFormat.exe");
                string containerPath = Path.Combine(hiddenFolder, "vc-container.vc");

                if (!File.Exists(veraCryptExe))
                {
                    LogMessage("Không tìm thấy VeraCryptFormat.exe trên USB!", true);
                    MessageBox.Show("Không tìm thấy VeraCryptFormat.exe trên USB!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }

                // Chuẩn bị tham số
                long availableSpace = drive.AvailableFreeSpace;
                LogMessage($"Không gian khả dụng: {availableSpace / (1024 * 1024)} MB");
                LogMessage("Đang tạo container...");
                UpdateProgress(70);

                string arguments = $"/create \"{containerPath}\" /size \"{availableSpace / (1024 * 1024)}\"M /password \"{txtPw1.Text}\" /encryption AES /filesystem FAT /quick /FastCreateFile /silent";

                // Chuẩn bị thông tin tiến trình
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = veraCryptExe,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                // Thực thi tiến trình
                using (Process process = Process.Start(psi))
                {
                    LogMessage("Đang tạo container (quá trình này có thể mất vài phút)...");
                    await Task.Run(() => process.WaitForExit());

                    bool success = process.ExitCode == 0;
                    if (success)
                    {
                        LogMessage("Tạo container thành công.");
                        UpdateProgress(90);
                    }
                    else
                    {
                        LogMessage($"Lỗi khi tạo container(Exit code: {process.ExitCode}).", true);
                    }
                    return success;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Lỗi khi tạo container: {ex.Message}", true);
                MessageBox.Show($"Lỗi khi tạo container: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private async Task CopyDirectoryAsync(string sourceDir, string destDir)
        {
            LogMessage($"Đang sao chép dữ liệu từ {sourceDir} đến {destDir}...");
            UpdateProgress(40);

            await Task.Run(() => {
                CopyDirectoryInternal(sourceDir, destDir);
            });

            // Ẩn các thư mục quan trọng
            if (Directory.Exists(destDir))
            {
                LogMessage("Đang cấu hình thuộc tính thư mục...");
                UpdateProgress(50);
                HideDirectory(destDir, VERACRYPT_HIDDEN_FOLDER);
                HideDirectory(destDir, AUTORUN_FOLDER);
            }
        }

        private void CopyDirectoryInternal(string sourceDir, string destDir)
        {
            try
            {
                // Kiểm tra thư mục nguồn
                if (!Directory.Exists(sourceDir))
                {
                    LogMessage($"Thư mục nguồn không tồn tại: {sourceDir}", true);
                    return;
                }

                // Tạo thư mục đích nếu cần
                Directory.CreateDirectory(destDir);

                // Sao chép tất cả các file trong thư mục hiện tại
                foreach (string file in Directory.GetFiles(sourceDir))
                {
                    string destFile = Path.Combine(destDir, Path.GetFileName(file));
                    File.Copy(file, destFile, true);
                    LogMessage($"Đã sao chép: {Path.GetFileName(file)}");
                }

                // Sao chép các thư mục con đệ quy
                foreach (string subDir in Directory.GetDirectories(sourceDir))
                {
                    string subDirName = Path.GetFileName(subDir);
                    LogMessage($"Đang sao chép thư mục: {subDirName}...");
                    string destSubDir = Path.Combine(destDir, subDirName);
                    CopyDirectoryInternal(subDir, destSubDir);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Lỗi sao chép thư mục: {ex.Message}", true);
                MessageBox.Show($"Lỗi sao chép thư mục: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void HideDirectory(string parentDir, string dirName)
        {
            try
            {
                string fullPath = Path.Combine(parentDir, dirName);
                if (Directory.Exists(fullPath))
                {
                    // Ẩn thư mục
                    DirectoryInfo dirInfo = new DirectoryInfo(fullPath);
                    if ((dirInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                    {
                        dirInfo.Attributes |= FileAttributes.Hidden;
                        LogMessage($"Đã ẩn thư mục: {dirName}");
                    }
                }
                else
                {
                    // Ẩn file
                    string fullFilePath = Path.Combine(parentDir, dirName);
                    if (File.Exists(fullFilePath))
                    {
                        FileInfo fileInfo = new FileInfo(fullFilePath);
                        if ((fileInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        {
                            fileInfo.Attributes |= FileAttributes.Hidden;
                            LogMessage($"Đã ẩn tệp tin: {dirName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log ra lỗi nhưng không hiển thị, vì đây không phải là lỗi nghiêm trọng
                LogMessage($"Lỗi khi ẩn {dirName}: {ex.Message}", true);
                Debug.WriteLine($"Lỗi khi ẩn {dirName}: {ex.Message}");
            }
        }

        private void LogMessage(string message, bool isError = false)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action<string, bool>(LogMessage), message, isError);
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string formattedMessage = $"[{timestamp}] {message}";

            txtLog.AppendText(formattedMessage + Environment.NewLine);

            // Tự động cuộn xuống cuối
            txtLog.SelectionStart = txtLog.Text.Length;
            txtLog.ScrollToCaret();

            // Cập nhật màu sắc nếu là lỗi
            if (isError)
            {
                // Lưu vị trí hiện tại
                int currentPosition = txtLog.SelectionStart;

                // Chọn dòng vừa thêm vào
                txtLog.Select(txtLog.Text.Length - formattedMessage.Length - Environment.NewLine.Length, formattedMessage.Length);

                // Khôi phục vị trí
                txtLog.SelectionStart = currentPosition;
                txtLog.SelectionLength = 0;
            }

            Application.DoEvents(); // Cho phép UI cập nhật
        }

        private void UpdateProgress(int value)
        {
            if (progressBar1.InvokeRequired)
            {
                progressBar1.Invoke(new Action<int>(UpdateProgress), value);
                return;
            }

            progressBar1.Value = value;
            Application.DoEvents(); // Cho phép UI cập nhật
        }

        private async void btnRun_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtPw1.Text))
            {
                MessageBox.Show("Vui lòng nhập mật khẩu!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            // Kiểm tra mật khẩu khớp nhau
            if (txtPw1.Text != txtPw2.Text)
            {
                MessageBox.Show("Mật khẩu không khớp. Vui lòng nhập lại!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (cbbUSB.SelectedItem == null)
            {
                MessageBox.Show("Vui lòng chọn thiết bị USB!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Vô hiệu hóa nút để tránh click nhiều lần
            btnRun.Enabled = false;
            btnReload.Enabled = false;
            btnReset.Enabled = false;
            Cursor = Cursors.WaitCursor;

            // Xóa log cũ và reset progress bar
            txtLog.Clear();
            progressBar1.Value = 0;

            try
            {
                string selectedUSB = cbbUSB.SelectedItem.ToString();
                LogMessage($"Đã chọn thiết bị: {selectedUSB}");
                int diskNumber = -1;

                // Tìm số Disk tương ứng
                UpdateProgress(5);
                LogMessage("Đang xác định số disk...");
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive WHERE InterfaceType='" + USB_INTERFACE_TYPE + "'"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string model = device["Model"]?.ToString() ?? "";
                        if (model == selectedUSB)
                        {
                            string deviceId = device["DeviceID"]?.ToString();
                            diskNumber = ExtractDiskNumber(deviceId);
                            LogMessage($"Đã xác định được số disk: {diskNumber}");
                            break;
                        }
                    }
                }

                if (diskNumber == -1)
                {
                    LogMessage("Không xác định được số disk.", true);
                    MessageBox.Show("Không xác định được số disk.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Xác nhận từ người dùng
                DialogResult result = MessageBox.Show(
                    $"Bạn có chắc chắn muốn format và cài đặt lên {selectedUSB} không?\n\nLƯU Ý: Tất cả dữ liệu trên thiết bị sẽ bị xóa!",
                    "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (result != DialogResult.Yes)
                {
                    LogMessage("Thao tác đã bị hủy bởi người dùng.");
                    return;
                }

                LogMessage("Bắt đầu tiến trình format USB...");
                UpdateProgress(10);

                // Thực thi diskpart với cơ chế retry
                bool diskpartSuccess = false;
                int retryCount = 0;
                const int MAX_RETRIES = 3;
                string diskpartErrorMessage = "";

                while (!diskpartSuccess && retryCount < MAX_RETRIES)
                {
                    try
                    {
                        // Tạo file script diskpart mới cho mỗi lần thử
                        string scriptPath = Path.GetTempFileName();
                        File.WriteAllText(scriptPath, $@"
select disk {diskNumber}
clean
create partition primary
format fs=ntfs quick
assign
exit
");
                        LogMessage($"Đang chạy diskpart lần thứ {retryCount + 1}...");

                        // Chuẩn bị process diskpart
                        ProcessStartInfo psi = new ProcessStartInfo("diskpart.exe", $"/s \"{scriptPath}\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };

                        using (Process process = Process.Start(psi))
                        {
                            await Task.Run(() => process.WaitForExit());

                            // Kiểm tra kết quả của diskpart
                            diskpartSuccess = process.ExitCode == 0;

                            if (diskpartSuccess)
                            {
                                LogMessage("Format USB thành công!");
                                UpdateProgress(20);
                            }
                            else
                            {
                                LogMessage($"Lỗi khi chạy diskpart (Exit code: {process.ExitCode}), đang thử lại...", true);
                                await Task.Delay(1000);
                            }
                        }

                        // Xóa file tạm
                        try { File.Delete(scriptPath); } catch { }
                    }
                    catch (Exception ex)
                    {
                        diskpartErrorMessage = ex.Message;
                        LogMessage($"Lỗi khi chạy diskpart: {ex.Message}", true);
                    }

                    retryCount++;

                    // Nếu đã thử đủ số lần mà vẫn thất bại
                    if (!diskpartSuccess && retryCount >= MAX_RETRIES)
                    {
                        string errorMsg = string.IsNullOrEmpty(diskpartErrorMessage)
                            ? "Không thể format USB sau nhiều lần thử"
                            : $"Lỗi khi chạy diskpart: {diskpartErrorMessage}";

                        LogMessage(errorMsg, true);
                        MessageBox.Show(errorMsg, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                // Chờ hệ thống nhận diện USB mới - tăng thời gian chờ
                LogMessage("Đang chờ hệ thống nhận diện USB mới...");
                await Task.Delay(3000);
                UpdateProgress(30);

                // Lấy ký tự ổ đĩa của USB sau khi format
                List<string> usbDrives = await WaitForUSBDriveLetters();
                if (usbDrives.Count == 0)
                {
                    LogMessage("Không tìm thấy phân vùng USB sau khi format.", true);
                    MessageBox.Show("Không tìm thấy phân vùng USB sau khi format. Vui lòng thử lại hoặc kiểm tra thiết bị.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string usbDrive = usbDrives[0]; // Lấy ổ đĩa đầu tiên
                LogMessage($"Đang sử dụng ổ đĩa: {usbDrive}");

                // Sao chép file từ thư mục bin vào USB
                string binFolder = Path.Combine(Application.StartupPath, "bin");
                if (!Directory.Exists(binFolder))
                {
                    LogMessage("Không tìm thấy thư mục bin trong ứng dụng!", true);
                    MessageBox.Show("Không tìm thấy thư mục bin trong ứng dụng!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                await CopyDirectoryAsync(binFolder, usbDrive);

                // Tạo container VeraCrypt
                bool containerCreated = await CreateVeraCryptContainer(usbDrive);
                if (containerCreated)
                {
                    LogMessage("Đã hoàn tất tất cả các thao tác!");
                    UpdateProgress(100);
                    MessageBox.Show($"Đã hoàn tất cài đặt vào USB ({usbDrive})!", "Hoàn thành", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    MessageBox.Show($"Vui lòng rút USB ra và cắm lại!", "Hoàn thành", MessageBoxButtons.OK, MessageBoxIcon.Information);

                }
                else
                {
                    LogMessage("Cài đặt thành công nhưng không thể tạo container.", true);
                    UpdateProgress(95);
                    MessageBox.Show("Cài đặt thành công vui lòng rút/cắm USB ra và thử lại.", "Cảnh báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Lỗi không xác định: {ex.Message}", true);
                MessageBox.Show($"Lỗi không xác định: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Khôi phục giao diện
                btnRun.Enabled = true;
                btnReload.Enabled = true;
                btnReset.Enabled = true;
                Cursor = Cursors.Default;

                // Làm mới danh sách USB
                LoadUSBDevices();
            }
        }


        private void MainForm_Load(object sender, EventArgs e)
        {
            this.ActiveControl = btnRun;
        }

        private async void btnReset_Click(object sender, EventArgs e)
        {
            if (cbbUSB.SelectedItem == null)
            {
                MessageBox.Show("Vui lòng chọn thiết bị USB!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnReset.Enabled = false;
            Cursor = Cursors.WaitCursor;
            txtLog.Clear();
            progressBar1.Value = 0;

            try
            {
                string selectedUSB = cbbUSB.SelectedItem.ToString();
                LogMessage($"Đã chọn thiết bị: {selectedUSB}");
                int diskNumber = -1;

                // Tìm số Disk tương ứng
                UpdateProgress(5);
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive WHERE InterfaceType='" + USB_INTERFACE_TYPE + "'"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string model = device["Model"]?.ToString() ?? "";
                        if (model == selectedUSB)
                        {
                            string deviceId = device["DeviceID"]?.ToString();
                            diskNumber = ExtractDiskNumber(deviceId);
                            LogMessage($"Đã xác định được số disk: {diskNumber}");
                            break;
                        }
                    }
                }

                if (diskNumber == -1)
                {
                    LogMessage("Không xác định được số disk.", true);
                    MessageBox.Show("Không xác định được số disk.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DialogResult confirm = MessageBox.Show(
                    $"Bạn có chắc chắn muốn **reset** (xóa và format lại) {selectedUSB} không?\n\nDỮ LIỆU TRÊN USB SẼ BỊ MẤT!",
                    "Xác nhận Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (confirm != DialogResult.Yes)
                {
                    LogMessage("Thao tác reset đã bị hủy bởi người dùng.");
                    return;
                }

                LogMessage("Bắt đầu reset USB...");
                UpdateProgress(10);

                // Format USB
                string scriptPath = Path.GetTempFileName();
                File.WriteAllText(scriptPath, $@"
select disk {diskNumber}
clean
create partition primary
format fs=ntfs quick
assign
exit");

                ProcessStartInfo psi = new ProcessStartInfo("diskpart.exe", $"/s \"{scriptPath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process proc = Process.Start(psi))
                {
                    await Task.Run(() => proc.WaitForExit());
                    if (proc.ExitCode != 0)
                    {
                        LogMessage("Lỗi khi format USB bằng diskpart.", true);
                        MessageBox.Show("Không thể format USB.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                try { File.Delete(scriptPath); } catch { }

                LogMessage("Reset USB thành công.");
                UpdateProgress(100);
                MessageBox.Show("USB đã được reset và format lại thành công!", "Hoàn tất", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                LogMessage($"Lỗi khi reset USB: {ex.Message}", true);
                MessageBox.Show($"Lỗi khi reset USB: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnReset.Enabled = true;
                Cursor = Cursors.Default;
                LoadUSBDevices();
            }
        }

    }
}