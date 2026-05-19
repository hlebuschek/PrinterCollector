namespace PrinterCollector;

partial class Form1
{
    private System.ComponentModel.IContainer components = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    private ComboBox cmbPrinters = null!;
    private Button btnRefreshPrinters = null!;
    private TextBox txtSerial = null!;
    private Button btnQuery = null!;
    private CheckBox chkSkipConnCheck = null!;
    private Label lblConnIndicator = null!;
    private TextBox txtStatus = null!;
    private TextBox txtModel = null!;
    private TextBox txtPageCount = null!;
    private TextBox txtDeviceId = null!;
    private TextBox txtFolder = null!;
    private Button btnBrowseFolder = null!;
    private Button btnSave = null!;
    private GroupBox grpSchedule = null!;
    private Label lblScheduleState = null!;
    private Button btnScheduleRegister = null!;
    private Button btnScheduleUnregister = null!;
    private Button btnScheduleRunNow = null!;
    private GroupBox grpServer = null!;
    private Label lblApiEndpoint = null!;
    private TextBox txtApiEndpoint = null!;
    private Label lblMasterKey = null!;
    private TextBox txtMasterKey = null!;
    private Label lblCounterFormat = null!;
    private ComboBox cmbCounterFormat = null!;
    private Button btnTestConnection = null!;
    private Button btnResetToken = null!;
    private Label lblTokenStatus = null!;
    private TextBox txtLog = null!;
    private Label lblPrinter = null!;
    private Label lblSerial = null!;
    private Label lblConn = null!;
    private Label lblModel = null!;
    private Label lblPageCount = null!;
    private Label lblDeviceId = null!;
    private Label lblFolder = null!;
    private Label lblLog = null!;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        SuspendLayout();

        lblPrinter = new Label { Text = "Принтер:", Location = new Point(12, 15), AutoSize = true };
        cmbPrinters = new ComboBox
        {
            Location = new Point(150, 12),
            Size = new Size(390, 23),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbPrinters.SelectedIndexChanged += CmbPrinters_SelectedIndexChanged!;
        btnRefreshPrinters = new Button
        {
            Text = "Обновить",
            Location = new Point(548, 11),
            Size = new Size(90, 25)
        };
        btnRefreshPrinters.Click += BtnRefreshPrinters_Click!;

        lblSerial = new Label { Text = "Серийный номер:", Location = new Point(12, 47), AutoSize = true };
        txtSerial = new TextBox { Location = new Point(150, 44), Size = new Size(488, 23) };

        btnQuery = new Button
        {
            Text = "Опросить принтер",
            Location = new Point(150, 78),
            Size = new Size(160, 30)
        };
        btnQuery.Click += BtnQuery_Click!;
        chkSkipConnCheck = new CheckBox
        {
            Text = "Пропустить проверку подключения (тест)",
            Location = new Point(320, 84),
            AutoSize = true
        };
        chkSkipConnCheck.CheckedChanged += ChkSkipConnCheck_CheckedChanged!;

        lblConn = new Label { Text = "Подключение:", Location = new Point(12, 122), AutoSize = true };
        lblConnIndicator = new Label
        {
            Location = new Point(150, 119),
            Size = new Size(16, 16),
            BackColor = Color.LightGray,
            BorderStyle = BorderStyle.FixedSingle
        };
        txtStatus = new TextBox
        {
            Location = new Point(174, 119),
            Size = new Size(464, 23),
            ReadOnly = true,
            BackColor = SystemColors.Control
        };

        lblModel = new Label { Text = "Модель:", Location = new Point(12, 152), AutoSize = true };
        txtModel = new TextBox
        {
            Location = new Point(150, 149),
            Size = new Size(488, 23),
            ReadOnly = true,
            BackColor = SystemColors.Control
        };

        lblPageCount = new Label { Text = "PageCount:", Location = new Point(12, 182), AutoSize = true };
        txtPageCount = new TextBox
        {
            Location = new Point(150, 179),
            Size = new Size(488, 23),
            ReadOnly = true,
            BackColor = SystemColors.Control
        };

        lblDeviceId = new Label { Text = "USB-устройство:", Location = new Point(12, 212), AutoSize = true };
        txtDeviceId = new TextBox
        {
            Location = new Point(150, 209),
            Size = new Size(488, 23),
            ReadOnly = true,
            BackColor = SystemColors.Control,
            Font = new Font("Consolas", 9F)
        };

        lblFolder = new Label { Text = "Папка XML:", Location = new Point(12, 250), AutoSize = true };
        txtFolder = new TextBox { Location = new Point(150, 247), Size = new Size(390, 23) };
        txtFolder.Leave += TxtFolder_Leave!;
        btnBrowseFolder = new Button
        {
            Text = "Обзор...",
            Location = new Point(548, 246),
            Size = new Size(90, 25)
        };
        btnBrowseFolder.Click += BtnBrowseFolder_Click!;

        btnSave = new Button
        {
            Text = "Сохранить и отправить",
            Location = new Point(150, 280),
            Size = new Size(180, 30),
            Enabled = false
        };
        btnSave.Click += BtnSave_Click!;

        grpServer = new GroupBox
        {
            Text = "Сервер (отправка опросов)",
            Location = new Point(12, 320),
            Size = new Size(626, 130)
        };
        lblApiEndpoint = new Label { Text = "API endpoint:", Location = new Point(10, 25), AutoSize = true };
        txtApiEndpoint = new TextBox
        {
            Location = new Point(130, 22),
            Size = new Size(484, 23),
            PlaceholderText = "http://localhost:8000/api/v1/inventory"
        };
        txtApiEndpoint.Leave += TxtApiEndpoint_Leave!;
        lblMasterKey = new Label { Text = "Master key:", Location = new Point(10, 53), AutoSize = true };
        txtMasterKey = new TextBox
        {
            Location = new Point(130, 50),
            Size = new Size(484, 23),
            UseSystemPasswordChar = true
        };
        txtMasterKey.Leave += TxtMasterKey_Leave!;
        lblCounterFormat = new Label { Text = "Формат счётчика:", Location = new Point(10, 81), AutoSize = true };
        cmbCounterFormat = new ComboBox
        {
            Location = new Point(130, 78),
            Size = new Size(180, 23),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbCounterFormat.Items.AddRange(new object[]
        {
            Services.CounterFormat.Total,
            Services.CounterFormat.BwA4,
            Services.CounterFormat.ColorA4,
            Services.CounterFormat.BwA3,
            Services.CounterFormat.ColorA3
        });
        cmbCounterFormat.SelectedIndexChanged += CmbCounterFormat_SelectedIndexChanged!;
        btnTestConnection = new Button
        {
            Text = "Тест соединения",
            Location = new Point(322, 77),
            Size = new Size(150, 25)
        };
        btnTestConnection.Click += BtnTestConnection_Click!;
        btnResetToken = new Button
        {
            Text = "Сбросить токен",
            Location = new Point(478, 77),
            Size = new Size(140, 25)
        };
        btnResetToken.Click += BtnResetToken_Click!;
        lblTokenStatus = new Label
        {
            Location = new Point(10, 107),
            Size = new Size(606, 18),
            Text = "Токен: неизвестно"
        };
        grpServer.Controls.AddRange(new Control[]
        {
            lblApiEndpoint, txtApiEndpoint,
            lblMasterKey, txtMasterKey,
            lblCounterFormat, cmbCounterFormat,
            btnTestConnection, btnResetToken,
            lblTokenStatus
        });

        grpSchedule = new GroupBox
        {
            Text = "Служба автосбора (LocalService, ежечасно)",
            Location = new Point(12, 460),
            Size = new Size(626, 75)
        };
        lblScheduleState = new Label
        {
            Location = new Point(10, 22),
            Size = new Size(606, 18),
            Text = "Состояние: неизвестно"
        };
        btnScheduleRegister = new Button
        {
            Text = "Установить службу",
            Location = new Point(10, 42),
            Size = new Size(150, 25)
        };
        btnScheduleRegister.Click += BtnScheduleRegister_Click!;
        btnScheduleUnregister = new Button
        {
            Text = "Удалить службу",
            Location = new Point(166, 42),
            Size = new Size(150, 25)
        };
        btnScheduleUnregister.Click += BtnScheduleUnregister_Click!;
        btnScheduleRunNow = new Button
        {
            Text = "Опросить сейчас",
            Location = new Point(322, 42),
            Size = new Size(150, 25)
        };
        btnScheduleRunNow.Click += BtnScheduleRunNow_Click!;
        grpSchedule.Controls.AddRange(new Control[]
        {
            lblScheduleState, btnScheduleRegister, btnScheduleUnregister, btnScheduleRunNow
        });

        lblLog = new Label { Text = "Лог:", Location = new Point(12, 545), AutoSize = true };
        txtLog = new TextBox
        {
            Location = new Point(12, 565),
            Size = new Size(626, 130),
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            ReadOnly = true,
            BackColor = SystemColors.Control,
            Font = new Font("Consolas", 9F),
            WordWrap = false
        };

        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(656, 710);
        Controls.AddRange(new Control[]
        {
            lblPrinter, cmbPrinters, btnRefreshPrinters,
            lblSerial, txtSerial,
            btnQuery, chkSkipConnCheck,
            lblConn, lblConnIndicator, txtStatus,
            lblModel, txtModel,
            lblPageCount, txtPageCount,
            lblDeviceId, txtDeviceId,
            lblFolder, txtFolder, btnBrowseFolder,
            btnSave,
            grpServer,
            grpSchedule,
            lblLog, txtLog
        });
        Text = "Printer Collector";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ResumeLayout(false);
        PerformLayout();
    }
}
