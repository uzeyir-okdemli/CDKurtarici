using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDKurtarici;

public class MainForm : Form
{
    // Renk Paleti (Modern Koyu Teknik Tema)
    private static readonly Color BgDark = Color.FromArgb(18, 22, 31);
    private static readonly Color BgPanel = Color.FromArgb(26, 32, 44);
    private static readonly Color BgCard = Color.FromArgb(34, 42, 58);
    private static readonly Color BorderColor = Color.FromArgb(52, 64, 86);
    private static readonly Color TextPrimary = Color.FromArgb(237, 242, 247);
    private static readonly Color TextMuted = Color.FromArgb(160, 174, 192);
    private static readonly Color AccentBlue = Color.FromArgb(56, 189, 248);
    private static readonly Color AccentGreen = Color.FromArgb(34, 197, 94);
    private static readonly Color AccentAmber = Color.FromArgb(245, 158, 11);
    private static readonly Color AccentRed = Color.FromArgb(239, 68, 68);

    // Üst Kontroller
    private ComboBox _cmbDrives = null!;
    private CheckBox _chkShowAllDrives = null!;
    private Button _btnRefreshDrives = null!;
    private Button _btnPickCustomFolder = null!;
    private Button _btnScanDisc = null!;
    private Button _btnEject = null!;
    private Button _btnLoadTray = null!;
    private TextBox _txtDestination = null!;
    private Button _btnBrowseDest = null!;
    private Button _btnOpenDest = null!;

    // Ayar Kontrolleri
    private ComboBox _cmbStrategy = null!;
    private ComboBox _cmbSpeedProfile = null!;
    private NumericUpDown _numRetries = null!;
    private NumericUpDown _numMaxConsecutiveBad = null!;
    private NumericUpDown _numTimeoutMs = null!;
    private CheckBox _chkKeepPartial = null!;

    // Orta Kontroller
    private DataGridView _gridFiles = null!;
    private Label _lblFileListSummary = null!;
    private SectorMapControl _sectorMapControl = null!;
    private Label _lblSpeedValue = null!;
    private Label _lblPerfectValue = null!;
    private Label _lblPartialValue = null!;
    private Label _lblSkippedValue = null!;
    private ListBox _lstLog = null!;

    // Alt Kontroller
    private Label _lblPhaseStatus = null!;
    private Label _lblCurrentFileStatus = null!;
    private ProgressBar _prgCurrentFile = null!;
    private ProgressBar _prgOverall = null!;
    private Button _btnStart = null!;
    private Button _btnPause = null!;
    private Button _btnSkipFile = null!;
    private Button _btnStop = null!;
    private Button _btnExportReport = null!;
    private Button _btnRepairVcdVideo = null!;

    // Motor ve Durum
    private readonly CdRecoveryEngine _engine = new();
    private List<RecoverableFileItem> _files = new();
    private CancellationTokenSource? _activeCts;
    private bool _isBusy;
    private string _customSourcePath = "";

    public MainForm()
    {
        Text = "CD/DVD Tam Performans Veri Kurtarıcı — Sektör Tabanlı Çizik CD Okuma Motoru";
        Size = new Size(1340, 860);
        MinimumSize = new Size(1120, 740);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        DoubleBuffered = true;

        BuildUi();
        WireEngineEvents();
        PopulateDrives();
    }

    private void BuildUi()
    {
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
            BackColor = BgDark
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));  // Kaynak & Hedef
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));  // Kurtarma & Donanım Ayarları
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Tablo + Sektör Haritası
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132)); // İlerleme ve Aksiyon Butonları

        mainLayout.Controls.Add(BuildSourceDestPanel(), 0, 0);
        mainLayout.Controls.Add(BuildSettingsPanel(), 0, 1);
        mainLayout.Controls.Add(BuildCenterSplitPanel(), 0, 2);
        mainLayout.Controls.Add(BuildBottomControlPanel(), 0, 3);

        Controls.Add(mainLayout);
    }

    private Panel BuildSourceDestPanel()
    {
        var panel = CreateStyledPanel();
        panel.Padding = new Padding(12, 8, 12, 8);

        // 1. Satır: Kaynak CD/DVD Sürücü
        var lblSource = new Label
        {
            Text = "💿 Kaynak CD/DVD:",
            AutoSize = true,
            Location = new Point(12, 14),
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = AccentBlue
        };

        _cmbDrives = new ComboBox
        {
            Location = new Point(148, 10),
            Width = 310,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgCard,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat
        };

        _chkShowAllDrives = new CheckBox
        {
            Text = "Tüm Sürücüler",
            AutoSize = true,
            Location = new Point(468, 13),
            ForeColor = TextMuted
        };
        _chkShowAllDrives.CheckedChanged += (_, _) => PopulateDrives();

        _btnRefreshDrives = CreateFlatButton("🔄 Yenile", new Point(585, 9), new Size(86, 29), BgCard);
        _btnRefreshDrives.Click += (_, _) => PopulateDrives();

        _btnPickCustomFolder = CreateFlatButton("📁 Klasör Seç...", new Point(677, 9), new Size(112, 29), BgCard);
        _btnPickCustomFolder.Click += OnPickCustomFolderClick;

        _btnScanDisc = CreateFlatButton("🔍 CD İçeriğini Tara", new Point(795, 9), new Size(165, 29), Color.FromArgb(2, 132, 199));
        _btnScanDisc.Font = new Font("Segoe UI Semibold", 9.5f);
        _btnScanDisc.Click += async (_, _) => await ScanSelectedSourceAsync();

        _btnEject = CreateFlatButton("⏏ Tepsiyi Aç", new Point(968, 9), new Size(106, 29), BgCard);
        _btnEject.Click += (_, _) => HandleTrayCommand(eject: true);

        _btnLoadTray = CreateFlatButton("📥 Kapat", new Point(1080, 9), new Size(84, 29), BgCard);
        _btnLoadTray.Click += (_, _) => HandleTrayCommand(eject: false);

        // 2. Satır: Hedef Klasör
        var lblDest = new Label
        {
            Text = "💾 Kurtarma Hedefi:",
            AutoSize = true,
            Location = new Point(12, 52),
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = AccentGreen
        };

        string defaultDest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"CD_Kurtarilan_{DateTime.Now:yyyyMMdd_HHmm}");

        _txtDestination = new TextBox
        {
            Text = defaultDest,
            Location = new Point(148, 49),
            Width = 640,
            BackColor = BgCard,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle
        };

        _btnBrowseDest = CreateFlatButton("Hedef Seç...", new Point(795, 47), new Size(165, 29), BgCard);
        _btnBrowseDest.Click += OnBrowseDestinationClick;

        _btnOpenDest = CreateFlatButton("📂 Kurtarılan Klasörü Aç", new Point(968, 47), new Size(196, 29), BgCard);
        _btnOpenDest.Click += OnOpenDestinationClick;

        panel.Controls.AddRange(new Control[]
        {
            lblSource, _cmbDrives, _chkShowAllDrives, _btnRefreshDrives, _btnPickCustomFolder,
            _btnScanDisc, _btnEject, _btnLoadTray,
            lblDest, _txtDestination, _btnBrowseDest, _btnOpenDest
        });

        return panel;
    }

    private Panel BuildSettingsPanel()
    {
        var panel = CreateStyledPanel();
        panel.Padding = new Padding(12, 8, 12, 8);

        var lblStrat = new Label
        {
            Text = "⚡ Okuma Tekniği (Hata Durumunda):",
            AutoSize = true,
            Location = new Point(12, 10),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI Semibold", 9f)
        };

        _cmbStrategy = new ComboBox
        {
            Location = new Point(12, 32),
            Width = 395,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgCard,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat
        };
        _cmbStrategy.Items.AddRange(new object[]
        {
            "1) Akıllı 2 Aşamalı: Önce Sağlamları Hızla Kurtar, Sonra Çizikleri Kurtar (Önerilen)",
            "2) Sektör Bazlı Kurtar: Bozuk 2KB Sektörleri Atla, Dosyayı Kurtar ve Devam Et",
            "3) Anında Atlama: Okunamayan Dosyayı Hemen Bırak ve Sıradaki Dosyaya Geç"
        });
        _cmbStrategy.SelectedIndex = 0;

        var lblSpeed = new Label
        {
            Text = "🚀 CD-ROM Donanım Hızı (IOCTL):",
            AutoSize = true,
            Location = new Point(422, 10),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI Semibold", 9f)
        };

        _cmbSpeedProfile = new ComboBox
        {
            Location = new Point(422, 32),
            Width = 275,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgCard,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat
        };
        _cmbSpeedProfile.Items.AddRange(new object[]
        {
            "Tam Performans (Maksimum Hız - CAV)",
            "Adaptif Dengeli (Sağlamda Tam, Çizikte 8x)",
            "Hassas Okuma (4x - Titreşimsiz Derin Çizik)"
        });
        _cmbSpeedProfile.SelectedIndex = 0;

        var lblRetry = new Label
        {
            Text = "Tekrar Deneme:",
            AutoSize = true,
            Location = new Point(712, 10),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI Semibold", 9f)
        };

        _numRetries = new NumericUpDown
        {
            Location = new Point(712, 32),
            Width = 85,
            Minimum = 0,
            Maximum = 15,
            Value = 2,
            BackColor = BgCard,
            ForeColor = TextPrimary
        };

        var lblMaxBad = new Label
        {
            Text = "Ardışık Hata Atlama Limiti (Sektör):",
            AutoSize = true,
            Location = new Point(812, 10),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI Semibold", 9f)
        };

        _numMaxConsecutiveBad = new NumericUpDown
        {
            Location = new Point(812, 32),
            Width = 100,
            Minimum = 1,
            Maximum = 2000,
            Value = 32,
            BackColor = BgCard,
            ForeColor = TextPrimary
        };

        var lblTimeout = new Label
        {
            Text = "Zaman Aşımı (ms):",
            AutoSize = true,
            Location = new Point(1025, 10),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI Semibold", 9f)
        };

        _numTimeoutMs = new NumericUpDown
        {
            Location = new Point(1025, 32),
            Width = 95,
            Minimum = 800,
            Maximum = 20000,
            Increment = 500,
            Value = 3500,
            BackColor = BgCard,
            ForeColor = TextPrimary
        };

        _chkKeepPartial = new CheckBox
        {
            Text = "Kısmi kurtarılanları koru",
            Checked = true,
            AutoSize = true,
            Location = new Point(1132, 34),
            ForeColor = TextPrimary
        };

        panel.Controls.AddRange(new Control[]
        {
            lblStrat, _cmbStrategy,
            lblSpeed, _cmbSpeedProfile,
            lblRetry, _numRetries,
            lblMaxBad, _numMaxConsecutiveBad,
            lblTimeout, _numTimeoutMs,
            _chkKeepPartial
        });

        return panel;
    }

    private Control BuildCenterSplitPanel()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 780,
            SplitterWidth = 8,
            BackColor = BgDark
        };

        // SOL PANEL: Dosya Tablosu
        var leftPanel = CreateStyledPanel();
        leftPanel.Dock = DockStyle.Fill;
        leftPanel.Padding = new Padding(8);

        var leftHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            BackColor = BgPanel
        };

        _lblFileListSummary = new Label
        {
            Text = "📋 CD Dosya Listesi (Başlamak için 'CD İçeriğini Tara' veya 'Kurtarmayı Başlat' butonuna basın)",
            AutoSize = true,
            Location = new Point(4, 7),
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = TextPrimary
        };

        var btnSelectAll = CreateFlatButton("Tümünü Seç", new Point(510, 2), new Size(92, 27), BgCard);
        btnSelectAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnSelectAll.Click += (_, _) => SetAllSelection(true);

        var btnSelectNone = CreateFlatButton("Seçimi Kaldır", new Point(608, 2), new Size(102, 27), BgCard);
        btnSelectNone.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnSelectNone.Click += (_, _) => SetAllSelection(false);

        var btnSelectFailed = CreateFlatButton("Sadece Hatalılar", new Point(716, 2), new Size(118, 27), BgCard);
        btnSelectFailed.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnSelectFailed.Click += (_, _) => SelectOnlyFailed();

        leftHeader.Controls.AddRange(new Control[] { _lblFileListSummary, btnSelectAll, btnSelectNone, btnSelectFailed });
        leftHeader.Resize += (_, _) =>
        {
            btnSelectFailed.Left = leftHeader.Width - btnSelectFailed.Width - 2;
            btnSelectNone.Left = btnSelectFailed.Left - btnSelectNone.Width - 6;
            btnSelectAll.Left = btnSelectNone.Left - btnSelectAll.Width - 6;
        };

        _gridFiles = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = BgDark,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = BorderColor,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            EnableHeadersVisualStyles = false
        };

        // Çift tamponlama (Double Buffering) ile akıcı tablo güncellemesi
        typeof(DataGridView).InvokeMember(
            "DoubleBuffered",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
            null,
            _gridFiles,
            new object[] { true });

        _gridFiles.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = BgCard,
            ForeColor = AccentBlue,
            Font = new Font("Segoe UI Semibold", 9f),
            Padding = new Padding(4),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        _gridFiles.ColumnHeadersHeight = 32;

        _gridFiles.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = BgDark,
            ForeColor = TextPrimary,
            SelectionBackColor = Color.FromArgb(30, 58, 95),
            SelectionForeColor = Color.White,
            Padding = new Padding(4, 2, 4, 2)
        };
        _gridFiles.RowTemplate.Height = 28;

        _gridFiles.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "ColSelect",
            HeaderText = "Seç",
            Width = 42,
            ReadOnly = false
        });
        _gridFiles.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColPath",
            HeaderText = "Dosya Yolu",
            Width = 280,
            ReadOnly = true
        });
        _gridFiles.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColSize",
            HeaderText = "Boyut",
            Width = 85,
            ReadOnly = true
        });
        _gridFiles.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColStatus",
            HeaderText = "Durum",
            Width = 175,
            ReadOnly = true
        });
        _gridFiles.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColPercent",
            HeaderText = "Kurtarılan",
            Width = 80,
            ReadOnly = true
        });
        _gridFiles.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColDetail",
            HeaderText = "Detay / Sektör Bilgisi",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true
        });

        _gridFiles.CellValueChanged += OnGridCellValueChanged;
        _gridFiles.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_gridFiles.IsCurrentCellDirty)
            {
                _gridFiles.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        leftPanel.Controls.Add(_gridFiles);
        leftPanel.Controls.Add(leftHeader);
        split.Panel1.Controls.Add(leftPanel);

        // SAĞ PANEL: Canlı Sektör Haritası + Performans Kartları + Günlük
        var rightPanel = CreateStyledPanel();
        rightPanel.Dock = DockStyle.Fill;
        rightPanel.Padding = new Padding(10);

        var rightLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = BgPanel
        };
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));  // 4 Telemetri Kartı
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 195)); // Canlı Sektör Haritası
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));  // Günlük Başlığı
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // İşlem Günlüğü

        // Telemetri Kartları
        var cardsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6)
        };
        for (int c = 0; c < 4; c++) cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));

        cardsGrid.Controls.Add(CreateMetricCard("ANLIK OKUMA HIZI", "0.0 MB/sn", AccentBlue, out _lblSpeedValue), 0, 0);
        cardsGrid.Controls.Add(CreateMetricCard("TAM KURTARILAN", "0", AccentGreen, out _lblPerfectValue), 1, 0);
        cardsGrid.Controls.Add(CreateMetricCard("KISMİ KURTARILAN", "0", AccentAmber, out _lblPartialValue), 2, 0);
        cardsGrid.Controls.Add(CreateMetricCard("ATLANAN / ÇİZİK", "0 / 0", AccentRed, out _lblSkippedValue), 3, 0);

        // Sektör Haritası
        var mapContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgCard,
            Padding = new Padding(8)
        };
        var lblMapTitle = new Label
        {
            Text = "🟩 Canlı Sektör Okuma Haritası  (Yeşil: 64KB Tam Hız | Sarı: 2KB Hassas | Kırmızı: Çizik/Atlandı)",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI Semibold", 8.5f),
            ForeColor = TextPrimary
        };
        _sectorMapControl = new SectorMapControl
        {
            Dock = DockStyle.Fill
        };
        mapContainer.Controls.Add(_sectorMapControl);
        mapContainer.Controls.Add(lblMapTitle);

        var lblLogHeader = new Label
        {
            Text = "📜 Canlı Donanım ve Kurtarma Günlüğü:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI Semibold", 9f),
            ForeColor = AccentBlue
        };

        _lstLog = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = BgDark,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.8f),
            HorizontalScrollbar = true
        };

        rightLayout.Controls.Add(cardsGrid, 0, 0);
        rightLayout.Controls.Add(mapContainer, 0, 1);
        rightLayout.Controls.Add(lblLogHeader, 0, 2);
        rightLayout.Controls.Add(_lstLog, 0, 3);

        rightPanel.Controls.Add(rightLayout);
        split.Panel2.Controls.Add(rightPanel);

        return split;
    }

    private Panel BuildBottomControlPanel()
    {
        var panel = CreateStyledPanel();
        panel.Padding = new Padding(12, 8, 12, 8);

        _lblPhaseStatus = new Label
        {
            Text = "Hazır — CD sürücünüzü seçip 'KURTARMAYI BAŞLAT' butonuna tıklayın.",
            AutoSize = false,
            Location = new Point(12, 8),
            Size = new Size(1100, 20),
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = AccentBlue,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _lblCurrentFileStatus = new Label
        {
            Text = "Dosya: -",
            AutoSize = false,
            Location = new Point(12, 30),
            Size = new Size(1100, 19),
            ForeColor = TextMuted,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _prgCurrentFile = new ProgressBar
        {
            Location = new Point(12, 52),
            Size = new Size(620, 14),
            Maximum = 1000,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        _prgOverall = new ProgressBar
        {
            Location = new Point(645, 52),
            Size = new Size(640, 14),
            Maximum = 1000,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _btnStart = CreateFlatButton("▶ KURTARMAYI BAŞLAT", new Point(12, 76), new Size(255, 42), Color.FromArgb(22, 163, 74));
        _btnStart.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _btnStart.Click += async (_, _) => await StartRecoveryAsync();

        _btnRepairVcdVideo = CreateFlatButton("🎬 VCD VİDEO ONAR, NETLEŞTİR & 50 FPS AKICILAŞTIR (AVSEQ.DAT)", new Point(275, 76), new Size(415, 42), Color.FromArgb(124, 58, 237));
        _btnRepairVcdVideo.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _btnRepairVcdVideo.Click += async (_, _) => await OnRepairVcdVideoClickAsync();

        _btnSkipFile = CreateFlatButton("⏭ DOSYAYI ATLA", new Point(698, 76), new Size(165, 42), Color.FromArgb(217, 119, 6));
        _btnSkipFile.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _btnSkipFile.Enabled = false;
        _btnSkipFile.Click += (_, _) =>
        {
            _engine.RequestSkipCurrentFile();
            AppendLog("⏭ Kullanıcı komutu: Mevcut dosya atlanıyor, sıradaki dosyaya geçiliyor...");
        };

        _btnPause = CreateFlatButton("⏸ Duraklat", new Point(871, 76), new Size(115, 42), BgCard);
        _btnPause.Enabled = false;
        _btnPause.Click += OnPauseResumeClick;

        _btnStop = CreateFlatButton("⏹ Durdur", new Point(994, 76), new Size(110, 42), Color.FromArgb(185, 28, 28));
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => StopRecovery();

        _btnExportReport = CreateFlatButton("📄 Rapor", new Point(1112, 76), new Size(120, 42), BgCard);
        _btnExportReport.Click += OnExportReportClick;

        panel.Controls.AddRange(new Control[]
        {
            _lblPhaseStatus, _lblCurrentFileStatus,
            _prgCurrentFile, _prgOverall,
            _btnStart, _btnRepairVcdVideo, _btnSkipFile, _btnPause, _btnStop, _btnExportReport
        });

        return panel;
    }

    private void WireEngineEvents()
    {
        _engine.OnLog += msg =>
        {
            if (IsDisposed) return;
            BeginInvoke(() => AppendLog(msg));
        };

        _engine.OnFileUpdated += item =>
        {
            if (IsDisposed) return;
            BeginInvoke(() => UpdateGridRow(item));
        };

        _engine.OnProgress += snap =>
        {
            if (IsDisposed) return;
            BeginInvoke(() => UpdateProgressUi(snap));
        };
    }

    private void PopulateDrives()
    {
        _cmbDrives.Items.Clear();
        _customSourcePath = "";

        var drives = NativeCdIo.GetDrivesNonBlocking(_chkShowAllDrives.Checked);
        if (drives.Count == 0 && !_chkShowAllDrives.Checked)
        {
            // Eğer hiç optik sürücü yoksa tüm sürücüleri listele
            drives = NativeCdIo.GetDrivesNonBlocking(includeAllDrives: true);
        }

        int cdIndex = -1;
        for (int i = 0; i < drives.Count; i++)
        {
            _cmbDrives.Items.Add(drives[i]);
            if (drives[i].IsCdRom && cdIndex == -1)
            {
                cdIndex = i;
            }
        }

        if (_cmbDrives.Items.Count > 0)
        {
            _cmbDrives.SelectedIndex = cdIndex >= 0 ? cdIndex : 0;
        }

        AppendLog($"Sürücü listesi güncellendi ({_cmbDrives.Items.Count} sürücü bulundu).");
    }

    private string GetSelectedSourceRoot()
    {
        if (!string.IsNullOrEmpty(_customSourcePath))
        {
            return _customSourcePath;
        }
        if (_cmbDrives.SelectedItem is DriveItem di)
        {
            return di.RootPath;
        }
        return "";
    }

    private void OnPickCustomFolderClick(object? sender, EventArgs e)
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "Kurtarılacak CD/DVD klasörünü veya dizini seçin",
            UseDescriptionForTitle = true
        };
        if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
        {
            _customSourcePath = fbd.SelectedPath;
            var customItem = new DriveItem
            {
                DriveLetter = fbd.SelectedPath.Substring(0, Math.Min(2, fbd.SelectedPath.Length)),
                RootPath = fbd.SelectedPath,
                IsCdRom = false,
                DisplayText = $"{fbd.SelectedPath}  [Özel Klasör]"
            };
            _cmbDrives.Items.Add(customItem);
            _cmbDrives.SelectedItem = customItem;
            AppendLog($"Kaynak klasör seçildi: {fbd.SelectedPath}");
        }
    }

    private void OnBrowseDestinationClick(object? sender, EventArgs e)
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "Kurtarılan dosyaların kaydedileceği hedef klasörü seçin",
            UseDescriptionForTitle = true,
            SelectedPath = _txtDestination.Text
        };
        if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
        {
            _txtDestination.Text = fbd.SelectedPath;
        }
    }

    private void OnOpenDestinationClick(object? sender, EventArgs e)
    {
        try
        {
            string path = _txtDestination.Text.Trim();
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Hedef klasör açılamadı: {ex.Message}", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void HandleTrayCommand(bool eject)
    {
        string src = GetSelectedSourceRoot();
        if (string.IsNullOrEmpty(src)) return;

        Task.Run(() =>
        {
            NativeCdIo.EjectOrLoadMedia(src, eject, out string msg);
            BeginInvoke(() => AppendLog($"[TEPSİ] {msg}"));
        });
    }

    private async Task ScanSelectedSourceAsync()
    {
        if (_isBusy) return;
        string sourceRoot = GetSelectedSourceRoot();
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            MessageBox.Show(this, "Lütfen önce bir CD/DVD sürücüsü veya klasör seçin.", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusyState(true, isScanningOnly: true);
        _gridFiles.Rows.Clear();
        _files.Clear();
        AppendLog($"CD dosya tablosu taranıyor: {sourceRoot} ...");

        _activeCts = new CancellationTokenSource();
        try
        {
            _files = await _engine.ScanDirectoryTreeAsync(
                sourceRoot,
                _activeCts.Token,
                status => BeginInvoke(() => _lblPhaseStatus.Text = status));

            PopulateGridFromFiles();
            long totalSize = 0;
            foreach (var f in _files) totalSize += f.SizeBytes;

            _lblFileListSummary.Text = $"📋 CD Dosya Listesi — {_files.Count} Dosya Bulundu (Toplam: {CdRecoveryEngine.FormatBytes(totalSize)})";
            _lblPhaseStatus.Text = $"Tarama tamamlandı: {_files.Count} dosya kurtarılmaya hazır.";
            AppendLog($"Tarama tamamlandı: {_files.Count} dosya ({CdRecoveryEngine.FormatBytes(totalSize)}) bulundu.");
        }
        catch (Exception ex)
        {
            AppendLog($"[HATA] Tarama sırasında hata: {ex.Message}");
        }
        finally
        {
            SetBusyState(false, isScanningOnly: true);
        }
    }

    private async Task StartRecoveryAsync()
    {
        if (_isBusy) return;

        string sourceRoot = GetSelectedSourceRoot();
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            MessageBox.Show(this, "Lütfen bir kaynak CD/DVD sürücüsü seçin.", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string destRoot = _txtDestination.Text.Trim();
        if (string.IsNullOrWhiteSpace(destRoot))
        {
            MessageBox.Show(this, "Lütfen kurtarılan dosyaların kaydedileceği hedef klasörü seçin.", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Eğer henüz taranmadıysa önce otomatik tara
        if (_files.Count == 0)
        {
            await ScanSelectedSourceAsync();
            if (_files.Count == 0)
            {
                MessageBox.Show(this, "Seçilen sürücüde okunabilir dosya bulunamadı veya CD takılı değil.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        var options = new RecoveryOptions
        {
            SourceRoot = sourceRoot,
            DestinationRoot = destRoot,
            Strategy = (RecoveryStrategy)_cmbStrategy.SelectedIndex,
            SpeedProfile = (HardwareSpeedProfile)_cmbSpeedProfile.SelectedIndex,
            MaxRetriesPerSector = (int)_numRetries.Value,
            MaxConsecutiveBadSectors = (int)_numMaxConsecutiveBad.Value,
            ReadTimeoutMs = (int)_numTimeoutMs.Value,
            KeepPartialFiles = _chkKeepPartial.Checked
        };

        SetBusyState(true, isScanningOnly: false);
        _activeCts = new CancellationTokenSource();

        try
        {
            await _engine.RunRecoveryAsync(_files, options, _activeCts.Token);
            _lblPhaseStatus.Text = "✅ Kurtarma işlemi tamamlandı! Kurtarılan dosyaları hedef klasörde görebilirsiniz.";
        }
        catch (OperationCanceledException)
        {
            _lblPhaseStatus.Text = "⏹ Kurtarma işlemi kullanıcı tarafından durduruldu.";
            AppendLog("Kurtarma işlemi durduruldu.");
        }
        catch (Exception ex)
        {
            _lblPhaseStatus.Text = $"❌ Hata: {ex.Message}";
            AppendLog($"[KRİTİK HATA] {ex.Message}");
        }
        finally
        {
            SetBusyState(false, isScanningOnly: false);
        }
    }

    private void OnPauseResumeClick(object? sender, EventArgs e)
    {
        bool newPaused = !_engine.IsPaused;
        _engine.SetPaused(newPaused);
        _btnPause.Text = newPaused ? "▶ Devam Et" : "⏸ Duraklat";
        _lblPhaseStatus.Text = newPaused ? "⏸ İşlem duraklatıldı." : "▶ İşlem devam ediyor...";
        AppendLog(newPaused ? "İşlem duraklatıldı." : "İşlem devam ettirildi.");
    }

    private void StopRecovery()
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            _engine.SetPaused(false);
            _activeCts.Cancel();
            _engine.RequestSkipCurrentFile();
        }
    }

    private void SetBusyState(bool busy, bool isScanningOnly)
    {
        _isBusy = busy;
        _btnStart.Enabled = !busy;
        _btnRepairVcdVideo.Enabled = !busy;
        _btnScanDisc.Enabled = !busy;
        _cmbDrives.Enabled = !busy;
        _btnPickCustomFolder.Enabled = !busy;
        _btnSkipFile.Enabled = busy && !isScanningOnly;
        _btnPause.Enabled = busy && !isScanningOnly;
        _btnStop.Enabled = busy;
        if (!busy)
        {
            _engine.SetPaused(false);
            _btnPause.Text = "⏸ Duraklat";
        }
    }

    private async Task OnRepairVcdVideoClickAsync()
    {
        if (_isBusy) return;

        string initialDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string destCandidate = Path.Combine(_txtDestination.Text.Trim(), "MPEGAV");
        if (Directory.Exists(destCandidate))
        {
            initialDir = destCandidate;
        }
        else
        {
            try
            {
                string[] desktopDirs = Directory.GetDirectories(initialDir, "CD_Kurtarilan*");
                if (desktopDirs.Length > 0)
                {
                    Array.Sort(desktopDirs);
                    string latestMpegAv = Path.Combine(desktopDirs[^1], "MPEGAV");
                    if (Directory.Exists(latestMpegAv)) initialDir = latestMpegAv;
                }
            }
            catch { }
        }

        using var ofd = new OpenFileDialog
        {
            Title = "Onarılacak, Netleştirilecek ve 50 FPS Akıcılaştırılacak VCD Video Dosyasını (AVSEQ01.DAT) Seçin",
            Filter = "VCD Video Dosyaları (*.DAT;*.MPG)|*.DAT;*.dat;*.MPG;*.mpg|Tüm Dosyalar (*.*)|*.*",
            InitialDirectory = initialDir,
            FileName = "AVSEQ01.DAT"
        };

        if (ofd.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(ofd.FileName))
        {
            return;
        }

        string sourceDat = ofd.FileName;
        string outDir = Path.GetDirectoryName(sourceDat) ?? _txtDestination.Text.Trim();
        // Eğer kaynak doğrudan CD-ROM üzerinde ise hedef klasöre kaydet
        string? root = Path.GetPathRoot(sourceDat);
        if (!string.IsNullOrEmpty(root) && NativeCdIo.GetDriveTypeW(root) == NativeCdIo.DRIVE_CDROM)
        {
            outDir = _txtDestination.Text.Trim();
        }

        SetBusyState(true, isScanningOnly: true);
        _activeCts = new CancellationTokenSource();

        try
        {
            _lblPhaseStatus.Text = $"🎬 VCD Video Onarılıyor & 720p 50FPS Netleştiriliyor: {Path.GetFileName(sourceDat)}";
            AppendLog($"=== VCD Video Restorasyon Başlatıldı: {sourceDat} ===");

            var res = await VcdVideoRecovery.RecoverAndEnhanceVcdFileAsync(
                sourceDat,
                outDir,
                VideoEnhancementPreset.UltraRestoreMotion50Fps,
                msg => BeginInvoke(() => AppendLog(msg)),
                (perm, statusTxt) => BeginInvoke(() =>
                {
                    _prgCurrentFile.Value = Math.Clamp(perm, 0, 1000);
                    _prgOverall.Value = Math.Clamp(perm, 0, 1000);
                    _lblCurrentFileStatus.Text = statusTxt;
                }),
                _activeCts.Token);

            if (res.Success)
            {
                _lblPhaseStatus.Text = $"✅ {res.SummaryMessage}";
                AppendLog($"✅ {res.SummaryMessage}");
                string targetFileToSelect = File.Exists(res.EnhancedMp4Path) ? res.EnhancedMp4Path : res.CleanMpgPath;
                if (File.Exists(targetFileToSelect))
                {
                    string safeFullTarget = Path.GetFullPath(targetFileToSelect).Replace("\"", "");
                    string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = explorerPath,
                        Arguments = $"/select,\"{safeFullTarget}\"",
                        UseShellExecute = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _lblPhaseStatus.Text = $"❌ Video onarım hatası: {ex.Message}";
            AppendLog($"[HATA] Video onarım hatası: {ex.Message}");
        }
        finally
        {
            SetBusyState(false, isScanningOnly: true);
        }
    }

    private void PopulateGridFromFiles()
    {
        _gridFiles.SuspendLayout();
        _gridFiles.Rows.Clear();
        foreach (var item in _files)
        {
            int rowIdx = _gridFiles.Rows.Add(
                item.Selected,
                item.RelativePath,
                CdRecoveryEngine.FormatBytes(item.SizeBytes),
                item.StatusText,
                $"{item.RecoveryPercent:F0}%",
                item.DetailMessage);
            _gridFiles.Rows[rowIdx].Tag = item;
        }
        _gridFiles.ResumeLayout();
    }

    private void UpdateGridRow(RecoverableFileItem item)
    {
        if (item.Index < 0 || item.Index >= _gridFiles.Rows.Count) return;
        DataGridViewRow row = _gridFiles.Rows[item.Index];

        row.Cells[3].Value = item.StatusText;
        row.Cells[4].Value = $"{item.RecoveryPercent:F1}%";
        row.Cells[5].Value = item.DetailMessage;

        Color rowFore = item.Status switch
        {
            FileRecoveryStatus.Reading => AccentBlue,
            FileRecoveryStatus.RecoveredPerfect => AccentGreen,
            FileRecoveryStatus.RecoveredPartial => AccentAmber,
            FileRecoveryStatus.DeferredToPass2 => Color.FromArgb(192, 132, 252),
            FileRecoveryStatus.SkippedError => AccentRed,
            _ => TextPrimary
        };
        row.DefaultCellStyle.ForeColor = rowFore;

        if (item.Status == FileRecoveryStatus.Reading)
        {
            try
            {
                if (item.Index >= 0 && item.Index < _gridFiles.RowCount)
                {
                    _gridFiles.FirstDisplayedScrollingRowIndex = Math.Max(0, item.Index - 3);
                }
            }
            catch { }
        }
    }

    private void UpdateProgressUi(RecoveryProgressSnapshot snap)
    {
        _lblPhaseStatus.Text = snap.CurrentPhaseText;
        _lblCurrentFileStatus.Text = $"Okunan Dosya: {snap.CurrentFileName}  ({CdRecoveryEngine.FormatBytes(snap.CurrentFileOffset)} / {CdRecoveryEngine.FormatBytes(snap.CurrentFileSize)})";

        int filePermille = snap.CurrentFileSize > 0
            ? (int)Math.Clamp((snap.CurrentFileOffset * 1000L) / snap.CurrentFileSize, 0, 1000)
            : 1000;
        _prgCurrentFile.Value = filePermille;

        int totalPermille = snap.TotalBytesTarget > 0
            ? (int)Math.Clamp((snap.TotalBytesProcessed * 1000L) / snap.TotalBytesTarget, 0, 1000)
            : 0;
        _prgOverall.Value = totalPermille;

        double mbPerSec = snap.CurrentSpeedBytesPerSec / (1024.0 * 1024.0);
        int cdX = (int)Math.Round(snap.CurrentSpeedBytesPerSec / (150.0 * 1024.0));
        _lblSpeedValue.Text = mbPerSec >= 1.0
            ? $"{mbPerSec:F2} MB/sn (~{cdX}x)"
            : $"{(snap.CurrentSpeedBytesPerSec / 1024.0):F0} KB/sn";

        _lblPerfectValue.Text = snap.PerfectFiles.ToString();
        _lblPartialValue.Text = snap.PartialFiles.ToString();
        _lblSkippedValue.Text = $"{snap.SkippedFiles} / {snap.TotalBadSectors}";

        _sectorMapControl.UpdateMap(snap.SectorMap);
    }

    private void OnGridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
        if (_gridFiles.Rows[e.RowIndex].Tag is RecoverableFileItem item)
        {
            item.Selected = Convert.ToBoolean(_gridFiles.Rows[e.RowIndex].Cells[0].Value);
        }
    }

    private void SetAllSelection(bool selected)
    {
        foreach (DataGridViewRow row in _gridFiles.Rows)
        {
            row.Cells[0].Value = selected;
            if (row.Tag is RecoverableFileItem item) item.Selected = selected;
        }
    }

    private void SelectOnlyFailed()
    {
        foreach (DataGridViewRow row in _gridFiles.Rows)
        {
            if (row.Tag is RecoverableFileItem item)
            {
                bool failedOrPartial = item.Status == FileRecoveryStatus.SkippedError ||
                                       item.Status == FileRecoveryStatus.RecoveredPartial ||
                                       item.Status == FileRecoveryStatus.DeferredToPass2;
                row.Cells[0].Value = failedOrPartial;
                item.Selected = failedOrPartial;
            }
        }
    }

    private void OnExportReportClick(object? sender, EventArgs e)
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Kurtarma Raporunu Kaydet",
            Filter = "Metin Raporu (*.txt)|*.txt",
            FileName = $"CD_Kurtarma_Raporu_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("==================================================================");
        sb.AppendLine("   CD/DVD TAM PERFORMANS VERİ KURTARICI - İŞLEM RAPORU");
        sb.AppendLine($"   Tarih: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"   Kaynak: {GetSelectedSourceRoot()}");
        sb.AppendLine($"   Hedef : {_txtDestination.Text}");
        sb.AppendLine("==================================================================");
        sb.AppendLine();

        foreach (var f in _files)
        {
            sb.AppendLine($"[{f.StatusText,-24}] %{f.RecoveryPercent,5:F1} | Bozuk Sektör: {f.BadSectorsCount,4} | {f.RelativePath} ({CdRecoveryEngine.FormatBytes(f.SizeBytes)}) {f.DetailMessage}");
        }

        File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
        AppendLog($"Rapor kaydedildi: {sfd.FileName}");
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _lstLog.Items.Add(line);
        if (_lstLog.Items.Count > 1500)
        {
            _lstLog.Items.RemoveAt(0);
        }
        _lstLog.TopIndex = Math.Max(0, _lstLog.Items.Count - 1);
    }

    private static Panel CreateStyledPanel()
    {
        var p = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgPanel,
            Margin = new Padding(0, 0, 0, 8)
        };
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        return p;
    }

    private static Button CreateFlatButton(string text, Point location, Size size, Color backColor)
    {
        var btn = new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = BorderColor;
        btn.FlatAppearance.BorderSize = 1;
        return btn;
    }

    private static Panel CreateMetricCard(string title, string initialValue, Color valueColor, out Label valueLabel)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgCard,
            Margin = new Padding(3),
            Padding = new Padding(6, 4, 6, 4)
        };
        var lblTitle = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 18,
            Font = new Font("Segoe UI Semibold", 7.5f),
            ForeColor = TextMuted
        };
        valueLabel = new Label
        {
            Text = initialValue,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = valueColor
        };
        card.Controls.Add(valueLabel);
        card.Controls.Add(lblTitle);
        return card;
    }
}

/// <summary>
/// Çizik CD okunurken her bir sektör bloğunun durumunu (Tam hız sağlam, 2KB kurtarıldı, Çizik/Atlandı)
/// gerçek zamanlı görselleştiren özel çift-tamponlu grafik kontrolü.
/// </summary>
public class SectorMapControl : Control
{
    private byte[] _cells = new byte[CdRecoveryEngine.SECTOR_MAP_CELLS];

    public SectorMapControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void UpdateMap(byte[] newCells)
    {
        if (newCells == null || newCells.Length == 0) return;
        _cells = newCells;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.FromArgb(18, 22, 31));

        int cols = 24;
        int rows = 10;
        int pad = 2;

        float cellW = (Width - (cols + 1) * pad) / (float)cols;
        float cellH = (Height - (rows + 1) * pad) / (float)rows;
        if (cellW < 2 || cellH < 2) return;

        using var brushPending = new SolidBrush(Color.FromArgb(45, 55, 72));
        using var brushHealthy = new SolidBrush(Color.FromArgb(34, 197, 94));
        using var brushRetried = new SolidBrush(Color.FromArgb(245, 158, 11));
        using var brushBad = new SolidBrush(Color.FromArgb(239, 68, 68));

        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (idx >= _cells.Length) break;
                byte state = _cells[idx++];
                Brush b = state switch
                {
                    1 => brushHealthy,
                    2 => brushRetried,
                    3 => brushBad,
                    _ => brushPending
                };

                float x = pad + c * (cellW + pad);
                float y = pad + r * (cellH + pad);
                g.FillRectangle(b, x, y, cellW, cellH);
            }
        }
    }
}
