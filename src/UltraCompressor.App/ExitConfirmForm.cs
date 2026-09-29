namespace UltraCompressor.App.Bridge;

/// <summary>
/// Hộp thoại hỏi khi đóng cửa sổ đang xử lý. Ba lựa chọn vì "thoát" ở trạng thái này
/// không chỉ có một nghĩa: giữ kết quả để chạy tiếp, hoặc trả đĩa về như cũ.
/// </summary>
/// <remarks>
/// Dùng form riêng thay vì <c>MessageBox</c> vì MessageBox chỉ có ba nút trả về
/// Yes/No/Cancel — đúng số lựa chọn nhưng nhãn không nói được điều gì, và người dùng
/// bấm "Yes" mà không biết mình vừa chọn xoá bản gốc.
/// </remarks>
internal sealed class ExitConfirmForm : Form
{
    private readonly Button _stay = new() { Text = "Ở lại", DialogResult = DialogResult.Cancel, AutoSize = true };
    private readonly Button _save = new() { Text = "Lưu lại để chạy tiếp", AutoSize = true };
    private readonly Button _undo = new() { Text = "Hoàn tác rồi thoát", AutoSize = true };

    public ExitConfirmForm(AppHost.ExitPlan plan)
    {
        Text = "Đang xử lý — thao tác chưa xong";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 260);

        var title = new Label
        {
            Text = "Bạn đang nén dở. Thoát bây giờ thì xử lý các tệp chưa tới sẽ dừng lại.",
            AutoSize = false,
            Font = new Font(Font.FontFamily, Font.Size + 1f, FontStyle.Bold),
            Location = new Point(20, 18),
            Size = new Size(520, 24),
        };

        var detail = new Label
        {
            Text = Describe(plan),
            AutoSize = false,
            Location = new Point(20, 50),
            Size = new Size(520, 120),
        };

        var hint = new Label
        {
            Text = plan.IsBusy
                ? "Tệp đang nén sẽ chạy nốt rồi mới dừng, để không phải nén lại từ đầu."
                : "Không còn tệp nào đang chạy.",
            AutoSize = false,
            ForeColor = SystemColors.GrayText,
            Location = new Point(20, 176),
            Size = new Size(520, 20),
        };

        _undo.Click += (_, _) => { DialogResult = DialogResult.Yes; Close(); };
        _save.Click += (_, _) => { DialogResult = DialogResult.No; Close(); };
        _stay.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _undo.Enabled = plan.AppliedFiles > 0;
        _undo.Text = plan.AppliedFiles > 0
            ? $"Hoàn tác rồi thoát ({plan.AppliedFiles} tệp)"
            : "Hoàn tác rồi thoát";

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(12, 8, 12, 8),
        };

        buttons.Controls.Add(_undo);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_stay);

        Controls.Add(buttons);
        Controls.Add(hint);
        Controls.Add(detail);
        Controls.Add(title);

        AcceptButton = _undo;
        CancelButton = _stay;
    }

    /// <summary>Chọn được gì: Ở lại, hoàn tác rồi thoát, hay lưu để chạy tiếp.</summary>
    public AppHost.ExitChoice Choice => DialogResult switch
    {
        DialogResult.Yes => AppHost.ExitChoice.UndoAndExit,
        DialogResult.No => AppHost.ExitChoice.SaveAndExit,
        _ => AppHost.ExitChoice.Stay,
    };

    private static string Describe(AppHost.ExitPlan plan)
    {
        var lines = new List<string>();

        if (plan.ActiveFiles > 0)
            lines.Add($"• Đang nén {plan.ActiveFiles} tệp trong {plan.RunningJobs} job.");

        if (plan.AppliedFiles > 0)
        {
            lines.Add($"• {plan.AppliedFiles} tệp đã nén xong, bản gốc còn nằm trong tệp .bak.");
            lines.Add($"   {string.Join(", ", plan.AppliedJobNames)}");
        }

        if (plan.ActiveFiles == 0 && plan.AppliedFiles == 0)
            lines.Add("• Chưa nén xong tệp nào.");

        lines.Add(string.Empty);
        lines.Add(plan.AppliedFiles > 0
            ? "— Hoàn tác rồi thoát: trả bản gốc về như cũ, không còn lối quay lui."
            : "— Chưa có tệp nào cần hoàn tác.");
        lines.Add("— Lưu lại để chạy tiếp: giữ nguyên kết quả. Mở lại lần sau sẽ có nút Tiếp tục.");

        return string.Join(Environment.NewLine, lines);
    }
}
