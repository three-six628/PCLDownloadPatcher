using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PCLDownloadPatcher
{
    /// <summary>
    /// 下载修复工具主界面：仅文字说明 + “更新自动修复”复选框 + “修复”按钮（单通道）。
    /// </summary>
    public class MainForm : Form
    {
        CheckBox chkAutoFix;
        Button btnFix;

        public MainForm()
        {
            Text = "PCL 下载修复工具";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 168);
            Font = new Font("Microsoft YaHei UI", 9F);

            var lbl = new Label();
            lbl.Text = "这是 PCL 下载修复工具。\r\n用于恢复新版 PCL 中被屏蔽的原版游戏下载功能，\r\n以及被一并屏蔽的整合包安装功能。\r\n点击「修复」按钮，在弹出的文件选择框中选择\r\nPCL 主程序（Plain Craft Launcher 2.exe），确定后自动执行修复。";
            lbl.Location = new Point(16, 12);
            lbl.Size = new Size(368, 84);
            lbl.AutoSize = false;

            chkAutoFix = new CheckBox();
            chkAutoFix.Text = "更新自动修复";
            chkAutoFix.Location = new Point(16, 102);
            chkAutoFix.Size = new Size(200, 24);
            chkAutoFix.Checked = Program.ReadSettings().AutoFix;
            chkAutoFix.CheckedChanged += (s, e) =>
            {
                try { Program.WriteSettings(chkAutoFix.Checked); }
                catch { }
            };

            btnFix = new Button();
            btnFix.Text = "修复";
            btnFix.Location = new Point(300, 98);
            btnFix.Size = new Size(84, 32);
            btnFix.Click += BtnFix_Click;

            Controls.Add(lbl);
            Controls.Add(chkAutoFix);
            Controls.Add(btnFix);
        }

        void BtnFix_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择 PCL 主程序";
                ofd.Filter = "PCL 主程序|Plain Craft Launcher 2.exe|可执行文件|*.exe";
                ofd.FilterIndex = 1;
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                string src = ofd.FileName;
                string dst = Path.Combine(
                    Path.GetDirectoryName(src),
                    Path.GetFileNameWithoutExtension(src) + ".download_fixed.exe");
                try
                {
                    string patcherPath = Assembly.GetExecutingAssembly().Location;
                    PatchCore.Patch(src, dst, patcherPath);
                    Program.WriteSettings(chkAutoFix.Checked);
                    MessageBox.Show(this,
                        "修复完成。\r\n输出文件：" + dst + "\r\n\r\n请用修补后的文件替换原主程序后启动。",
                        "PCL 下载修复工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "修复失败：\r\n" + ex.Message,
                        "PCL 下载修复工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
