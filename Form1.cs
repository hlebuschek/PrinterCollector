using System.Drawing.Printing;
using PrinterCollector.Models;
using PrinterCollector.Services;

namespace PrinterCollector;

public partial class Form1 : Form
{
    private readonly PrinterReader _reader = new();
    private readonly OverrideStore _overrides = new();
    private readonly AppSettings _settings;
    private PrinterReading? _lastReading;
    private string? _lastAutoSerial;

    public Form1()
    {
        InitializeComponent();
        // Best-effort: если у нас admin, выставим permissive ACL на ProgramData\PrinterCollector\;
        // если нет — silent no-op, ACL должен быть установлен при последующем admin-запуске.
        AppSettings.EnsureBaseDirAccessible();
        _settings = AppSettings.Load();
        LoadPrinters();
        ApplyLoadedSettings();
        SetIndicator(IndicatorState.Unknown);
        RefreshScheduleState();
        Log($"Сборка: {System.Reflection.Assembly.GetExecutingAssembly().Location}");
        Log($"Settings: {AppSettings.DefaultPath}");
    }

    private enum IndicatorState { Unknown, Online, Offline }

    private void SetIndicator(IndicatorState state)
    {
        lblConnIndicator.BackColor = state switch
        {
            IndicatorState.Online => Color.LimeGreen,
            IndicatorState.Offline => Color.IndianRed,
            _ => Color.LightGray
        };
    }

    private void LoadPrinters()
    {
        cmbPrinters.Items.Clear();
        foreach (string p in PrinterSettings.InstalledPrinters)
            cmbPrinters.Items.Add(p);
    }

    private void ApplyLoadedSettings()
    {
        txtFolder.Text = _settings.OutputFolder;
        txtApiEndpoint.Text = _settings.ApiEndpoint;
        txtMasterKey.Text = _settings.MasterKey;
        cmbCounterFormat.SelectedItem = _settings.CounterFormat;
        chkSkipConnCheck.Checked = _settings.SkipConnectionCheck;
        UpdateTokenStatus();

        if (!string.IsNullOrEmpty(_settings.PrinterName))
        {
            var idx = cmbPrinters.Items.IndexOf(_settings.PrinterName);
            if (idx >= 0) cmbPrinters.SelectedIndex = idx;
            else if (cmbPrinters.Items.Count > 0) cmbPrinters.SelectedIndex = 0;
        }
        else if (cmbPrinters.Items.Count > 0)
        {
            cmbPrinters.SelectedIndex = 0;
        }
    }

    private void PersistSettings()
    {
        _settings.PrinterName = cmbPrinters.SelectedItem as string ?? "";
        _settings.OutputFolder = txtFolder.Text;
        _settings.ApiEndpoint = txtApiEndpoint.Text.Trim();
        _settings.MasterKey = txtMasterKey.Text;
        if (cmbCounterFormat.SelectedItem is CounterFormat cf)
            _settings.CounterFormat = cf;
        _settings.SkipConnectionCheck = chkSkipConnCheck.Checked;
        try { _settings.Save(); }
        catch (Exception ex) { Log("Не удалось сохранить settings.json: " + ex.Message); }
    }

    private void UpdateTokenStatus()
    {
        var hasToken = !string.IsNullOrEmpty(_settings.GetApiToken());
        lblTokenStatus.Text = hasToken
            ? $"Токен: получен (agent_id={_settings.AgentId})"
            : "Токен: не получен — будет запрошен при первой отправке";
        lblTokenStatus.ForeColor = hasToken ? Color.DarkGreen : Color.DarkOrange;
        btnResetToken.Enabled = hasToken;

        // Master key стирается из settings после успешной регистрации (security):
        // синхронизируем UI с состоянием settings, чтобы пользователь не видел,
        // что в поле висит ключ, который на самом деле уже стёрт на диске.
        if (txtMasterKey.Text != (_settings.MasterKey ?? ""))
            txtMasterKey.Text = _settings.MasterKey ?? "";
    }

    private async void BtnTestConnection_Click(object sender, EventArgs e)
    {
        PersistSettings();
        if (string.IsNullOrWhiteSpace(_settings.ApiEndpoint))
        {
            Log("Тест соединения: ApiEndpoint не задан");
            return;
        }

        SetBusy(true);
        Log($"Тест соединения: GET {_settings.ApiEndpoint}");
        try
        {
            using var client = new ApiClient(_settings, log: Log);
            var r = await client.TestConnectionAsync();
            Log(r.Success ? $"OK: {r.Message}" : $"ОШИБКА: {r.Message}");
        }
        finally { SetBusy(false); }
    }

    private void BtnResetToken_Click(object sender, EventArgs e)
    {
        _settings.SetApiToken("");
        try { _settings.Save(); } catch { }
        Log("Токен сброшен. При следующей отправке будет запрошена регистрация.");
        UpdateTokenStatus();
    }

    private void CmbCounterFormat_SelectedIndexChanged(object sender, EventArgs e) => PersistSettings();
    private void TxtApiEndpoint_Leave(object sender, EventArgs e) => PersistSettings();
    private void TxtMasterKey_Leave(object sender, EventArgs e) => PersistSettings();
    private void ChkSkipConnCheck_CheckedChanged(object sender, EventArgs e) => PersistSettings();

    private void CmbPrinters_SelectedIndexChanged(object sender, EventArgs e) => PersistSettings();
    private void TxtFolder_Leave(object sender, EventArgs e) => PersistSettings();
    private void BtnRefreshPrinters_Click(object sender, EventArgs e) => LoadPrinters();

    private void BtnBrowseFolder_Click(object sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog();
        if (Directory.Exists(txtFolder.Text)) dlg.SelectedPath = txtFolder.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            txtFolder.Text = dlg.SelectedPath;
            PersistSettings();
        }
    }

    private void BtnQuery_Click(object sender, EventArgs e)
    {
        if (cmbPrinters.SelectedItem is not string printerName)
        {
            MessageBox.Show(this, "Выберите принтер.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        _lastReading = null;
        _lastAutoSerial = null;
        btnSave.Enabled = false;
        Log($"--- Опрос «{printerName}» ---");

        try
        {
            var savedOverride = _overrides.Get(printerName);
            var result = _reader.Read(printerName, savedOverride, _settings.CounterFormat, _settings.SkipConnectionCheck, Log);
            if (_settings.SkipConnectionCheck)
                Log("Тестовый режим: проверка подключения отключена");

            if (result.Status != null)
            {
                txtDeviceId.Text = result.Status.DeviceInstanceId ?? "";
                Log($"Подключение: {result.Status.Reason}");
                if (result.Status.PnpStatus != null)
                    Log($"PnP Status='{result.Status.PnpStatus}', CMErr={result.Status.ConfigManagerErrorCode}");
            }

            _lastAutoSerial = result.AutoSerial;
            if (!string.IsNullOrEmpty(result.UsbDeviceId))
                Log($"USB-родитель: {result.UsbDeviceId}");
            if (result.AutoSerial != null)
                Log($"Серийник из USB-дескриптора: {result.AutoSerial}");
            else if (result.SerialDetectionWarning != null)
                Log($"Авто-серийник недоступен: {result.SerialDetectionWarning}");

            if (savedOverride != null)
                Log($"Применён сохранённый ручной серийник: {savedOverride}");

            if (!result.Success)
            {
                SetIndicator(result.Status?.IsOnline == true ? IndicatorState.Online : IndicatorState.Offline);
                txtStatus.Text = result.Error ?? "Неизвестная ошибка.";
                txtStatus.ForeColor = Color.DarkRed;
                txtModel.Clear();
                txtPageCount.Clear();
                if (savedOverride != null) txtSerial.Text = savedOverride;
                else if (result.AutoSerial != null) txtSerial.Text = result.AutoSerial;
                Log("ОШИБКА: " + result.Error);
                return;
            }

            var r = result.Reading!;
            _lastReading = r;
            SetIndicator(IndicatorState.Online);
            txtStatus.Text = "Принтер онлайн, данные считаны.";
            txtStatus.ForeColor = Color.DarkGreen;
            txtModel.Text = r.Model;
            txtPageCount.Text = r.Counters.TotalPages?.ToString() ?? "";
            txtSerial.Text = r.SerialNumber.Value;
            btnSave.Enabled = true;
            Log($"OK: PageCount={r.Counters.TotalPages}, Format={_settings.CounterFormat}, " +
                $"Model='{r.Model}', Serial='{r.SerialNumber.Value}' (source={r.SerialNumber.Source})");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnSave_Click(object sender, EventArgs e)
    {
        if (_lastReading == null) return;

        var serial = txtSerial.Text.Trim();
        if (string.IsNullOrEmpty(serial))
        {
            MessageBox.Show(this, "Серийный номер пуст.", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var printerName = _lastReading.PrinterName;
        var differsFromAuto = !string.Equals(serial, _lastAutoSerial, StringComparison.Ordinal);

        if (differsFromAuto)
        {
            _overrides.Set(printerName, serial);
            _lastReading.SerialNumber = new SerialInfo
            {
                Source = _settings.SkipConnectionCheck ? "test" : "manual",
                Value = serial
            };
            Log($"Сохранён оверрайд серийника для «{printerName}»: {serial}");
        }
        else
        {
            _overrides.Clear(printerName);
            _lastReading.SerialNumber = new SerialInfo
            {
                Source = _settings.SkipConnectionCheck ? "test" : "device",
                Value = serial
            };
        }

        PersistSettings();

        try
        {
            var path = ReadingStorage.Save(_lastReading, txtFolder.Text);
            Log("XML сохранён: " + path);
        }
        catch (Exception ex)
        {
            Log("Ошибка сохранения XML: " + ex.Message);
        }

        PersistSettings();

        if (string.IsNullOrWhiteSpace(_settings.ApiEndpoint))
        {
            Log("ApiEndpoint не задан — отправка пропущена");
            return;
        }
        if (string.IsNullOrEmpty(_settings.GetApiToken()) && string.IsNullOrWhiteSpace(_settings.MasterKey))
        {
            Log("Master key пуст и токен не получен — заполните «Master key» и нажмите Tab перед отправкой");
            return;
        }

        SetBusy(true);
        try
        {
            var queue = new OfflineQueue(log: Log);
            queue.Enqueue(_lastReading);
            Log($"В очереди: {queue.Count}");

            using var client = new ApiClient(_settings, log: Log);
            var flush = await queue.FlushAsync(client);
            UpdateTokenStatus();
            if (flush.Error != null)
                Log($"Отправка не удалась: {flush.Error}");
            else
                Log($"Отправлено: {flush.Sent}, осталось: {flush.Failed}");
        }
        finally { SetBusy(false); }
    }

    private void BtnScheduleRegister_Click(object sender, EventArgs e)
    {
        PersistSettings();
        if (string.IsNullOrWhiteSpace(_settings.PrinterName))
        {
            MessageBox.Show(this, "Сначала выберите принтер — он будет использован автосбором.",
                "Служба", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var exe = Application.ExecutablePath;
        var r = ServiceInstaller.Install(exe);
        Log($"sc create exit={r.ExitCode}: {r.Output}");
        if (r.ExitCode == 0)
        {
            // Сервис создан → per-service SID NT SERVICE\PrinterCollector теперь
            // резолвится через LSA, можно выдать ACE на %ProgramData%\PrinterCollector\.
            // Дополнительно даём BUILTIN\Users:M, чтобы GUI обычных юзеров мог писать.
            AppSettings.EnsureBaseDirAccessible();
            Log("ACL: BUILTIN\\Users:M + NT SERVICE\\PrinterCollector:M на " + Path.GetDirectoryName(AppSettings.DefaultPath));

            var start = ServiceInstaller.Start();
            Log($"sc start exit={start.ExitCode}: {start.Output}");

            MessageBox.Show(this,
                $"Служба «{ServiceInstaller.DisplayName}» установлена и запущена.\n" +
                $"Учётная запись: NT AUTHORITY\\LocalService (write-restricted, per-service SID).\n" +
                $"Команда: \"{exe}\" --service\n" +
                $"Лог сборщика: {AppSettings.DefaultLogPath}\n" +
                $"События службы: Event Viewer → Windows Logs → Application, source «{ServiceInstaller.ServiceName}».",
                "Служба", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else if (r.Output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                 || r.Output.Contains("отказано в доступе", StringComparison.OrdinalIgnoreCase)
                 || r.ExitCode == 5)
        {
            MessageBox.Show(this,
                "Установка службы требует прав администратора.\n\n" +
                "Закройте приложение, нажмите правой кнопкой на PrinterCollector.exe → " +
                "«Запустить от имени администратора», и установите службу ещё раз.\n\n" +
                "После установки админ-права больше не нужны — служба будет работать сама.",
                "Нужны права администратора", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(this, "sc create вернул ошибку:\n" + r.Output,
                "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshScheduleState();
    }

    private void BtnScheduleUnregister_Click(object sender, EventArgs e)
    {
        var r = ServiceInstaller.Uninstall();
        Log($"sc delete exit={r.ExitCode}: {r.Output}");
        RefreshScheduleState();
    }

    private void BtnScheduleRunNow_Click(object sender, EventArgs e)
    {
        // У службы нет встроенного «run once» — самый чистый способ дёрнуть тик
        // вручную: запустить PrinterCollector.exe --collect отдельным процессом,
        // он отработает один цикл и завершится, записав в тот же collect.log.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--collect");
            System.Diagnostics.Process.Start(psi);
            Log($"Запущен разовый тик (--collect). Смотри {AppSettings.DefaultLogPath} через несколько секунд.");
        }
        catch (Exception ex)
        {
            Log("Не удалось запустить разовый тик: " + ex.Message);
        }
    }

    private void RefreshScheduleState()
    {
        var status = ServiceInstaller.Status();
        var exists = status != null;
        lblScheduleState.Text = exists
            ? $"Состояние: служба «{ServiceInstaller.ServiceName}» установлена, статус: {status}."
            : "Состояние: служба не установлена.";
        btnScheduleRegister.Enabled = !exists;
        btnScheduleUnregister.Enabled = exists;
        btnScheduleRunNow.Enabled = !string.IsNullOrWhiteSpace(_settings.PrinterName);
    }

    private void Log(string message)
    {
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void SetBusy(bool busy)
    {
        btnQuery.Enabled = !busy;
        btnRefreshPrinters.Enabled = !busy;
        cmbPrinters.Enabled = !busy;
        txtSerial.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        Application.DoEvents();
    }
}
