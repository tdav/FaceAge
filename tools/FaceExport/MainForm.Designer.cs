partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Раскладка окна. Метод правит конструктор форм Visual Studio.</summary>
    private void InitializeComponent()
    {
        fields = new TableLayoutPanel();
        labelSql = new Label();
        sql = new TextBox();
        labelAge = new Label();
        ageRow = new FlowLayoutPanel();
        labelFrom = new Label();
        ageFrom = new NumericUpDown();
        labelTo = new Label();
        ageTo = new NumericUpDown();
        labelSexes = new Label();
        sexes = new TextBox();
        labelFolder = new Label();
        folder = new TextBox();
        browse = new Button();
        state = new TableLayoutPanel();
        head = new TableLayoutPanel();
        current = new Label();
        pairNumber = new Label();
        bar = new ProgressBar();
        counters = new Label();
        logPanel = new Panel();
        log = new TextBox();
        buttons = new FlowLayoutPanel();
        start = new Button();
        stop = new Button();
        fields.SuspendLayout();
        ageRow.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)ageFrom).BeginInit();
        ((System.ComponentModel.ISupportInitialize)ageTo).BeginInit();
        state.SuspendLayout();
        head.SuspendLayout();
        logPanel.SuspendLayout();
        buttons.SuspendLayout();
        SuspendLayout();
        //
        // fields
        //
        fields.AutoSize = true;
        fields.ColumnCount = 3;
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
        fields.Controls.Add(labelSql, 0, 0);
        fields.Controls.Add(sql, 1, 0);
        fields.Controls.Add(labelAge, 0, 1);
        fields.Controls.Add(ageRow, 1, 1);
        fields.Controls.Add(labelFolder, 0, 2);
        fields.Controls.Add(folder, 1, 2);
        fields.Controls.Add(browse, 2, 2);
        fields.Dock = DockStyle.Top;
        fields.Location = new Point(0, 0);
        fields.Name = "fields";
        fields.Padding = new Padding(10, 10, 10, 4);
        fields.RowCount = 3;
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 226F));
        fields.RowStyles.Add(new RowStyle());
        fields.RowStyles.Add(new RowStyle());
        fields.Size = new Size(820, 298);
        fields.TabIndex = 0;
        //
        // labelSql
        //
        labelSql.AutoSize = true;
        labelSql.Margin = new Padding(0, 6, 0, 0);
        labelSql.Name = "labelSql";
        labelSql.Size = new Size(71, 15);
        labelSql.TabIndex = 0;
        labelSql.Text = "SQL-запрос";
        //
        // sql
        //
        sql.AcceptsReturn = true;
        fields.SetColumnSpan(sql, 2);
        sql.Dock = DockStyle.Fill;
        sql.Font = new Font("Courier New", 9.5F);
        sql.Multiline = true;
        sql.Name = "sql";
        sql.ScrollBars = ScrollBars.Both;
        sql.Size = new Size(644, 220);
        sql.TabIndex = 1;
        sql.WordWrap = false;
        //
        // labelAge
        //
        labelAge.AutoSize = true;
        labelAge.Margin = new Padding(0, 6, 0, 0);
        labelAge.Name = "labelAge";
        labelAge.Size = new Size(84, 15);
        labelAge.TabIndex = 2;
        labelAge.Text = "Возраст и пол";
        //
        // ageRow
        //
        ageRow.AutoSize = true;
        fields.SetColumnSpan(ageRow, 2);
        ageRow.Controls.Add(labelFrom);
        ageRow.Controls.Add(ageFrom);
        ageRow.Controls.Add(labelTo);
        ageRow.Controls.Add(ageTo);
        ageRow.Controls.Add(labelSexes);
        ageRow.Controls.Add(sexes);
        ageRow.Dock = DockStyle.Fill;
        ageRow.Margin = new Padding(0);
        ageRow.Name = "ageRow";
        ageRow.Size = new Size(650, 29);
        ageRow.TabIndex = 3;
        ageRow.WrapContents = false;
        //
        // labelFrom
        //
        labelFrom.AutoSize = true;
        labelFrom.Margin = new Padding(0, 6, 3, 0);
        labelFrom.Name = "labelFrom";
        labelFrom.Size = new Size(19, 15);
        labelFrom.TabIndex = 0;
        labelFrom.Text = "от";
        //
        // ageFrom
        //
        ageFrom.Maximum = new decimal(new int[] { 120, 0, 0, 0 });
        ageFrom.Name = "ageFrom";
        ageFrom.Size = new Size(60, 23);
        ageFrom.TabIndex = 1;
        ageFrom.Value = new decimal(new int[] { 10, 0, 0, 0 });
        //
        // labelTo
        //
        labelTo.AutoSize = true;
        labelTo.Margin = new Padding(10, 6, 3, 0);
        labelTo.Name = "labelTo";
        labelTo.Size = new Size(20, 15);
        labelTo.TabIndex = 2;
        labelTo.Text = "до";
        //
        // ageTo
        //
        ageTo.Maximum = new decimal(new int[] { 120, 0, 0, 0 });
        ageTo.Name = "ageTo";
        ageTo.Size = new Size(60, 23);
        ageTo.TabIndex = 3;
        ageTo.Value = new decimal(new int[] { 55, 0, 0, 0 });
        //
        // labelSexes
        //
        labelSexes.AutoSize = true;
        labelSexes.Margin = new Padding(16, 6, 3, 0);
        labelSexes.Name = "labelSexes";
        labelSexes.Size = new Size(80, 15);
        labelSexes.TabIndex = 4;
        labelSexes.Text = "значения :sex";
        //
        // sexes
        //
        sexes.Name = "sexes";
        sexes.Size = new Size(90, 23);
        sexes.TabIndex = 5;
        sexes.Text = "1;2";
        //
        // labelFolder
        //
        labelFolder.AutoSize = true;
        labelFolder.Margin = new Padding(0, 6, 0, 0);
        labelFolder.Name = "labelFolder";
        labelFolder.Size = new Size(94, 15);
        labelFolder.TabIndex = 4;
        labelFolder.Text = "Папка экспорта";
        //
        // folder
        //
        folder.Dock = DockStyle.Fill;
        folder.Name = "folder";
        folder.Size = new Size(554, 23);
        folder.TabIndex = 5;
        //
        // browse
        //
        browse.Dock = DockStyle.Fill;
        browse.Name = "browse";
        browse.Size = new Size(84, 23);
        browse.TabIndex = 6;
        browse.Text = "Обзор";
        //
        // state
        //
        state.AutoSize = true;
        state.ColumnCount = 1;
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        state.Controls.Add(head, 0, 0);
        state.Controls.Add(bar, 0, 1);
        state.Controls.Add(counters, 0, 2);
        state.Dock = DockStyle.Top;
        state.Name = "state";
        state.Padding = new Padding(10, 6, 10, 4);
        state.RowCount = 3;
        state.RowStyles.Add(new RowStyle());
        state.RowStyles.Add(new RowStyle());
        state.RowStyles.Add(new RowStyle());
        state.Size = new Size(820, 74);
        state.TabIndex = 1;
        //
        // head
        //
        head.AutoSize = true;
        head.ColumnCount = 2;
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        head.ColumnStyles.Add(new ColumnStyle());
        head.Controls.Add(current, 0, 0);
        head.Controls.Add(pairNumber, 1, 0);
        head.Dock = DockStyle.Fill;
        head.Name = "head";
        head.RowCount = 1;
        head.RowStyles.Add(new RowStyle());
        head.Size = new Size(794, 15);
        head.TabIndex = 0;
        //
        // current
        //
        current.AutoSize = true;
        current.Name = "current";
        current.Size = new Size(109, 15);
        current.TabIndex = 0;
        current.Text = "Ожидание запуска";
        //
        // pairNumber
        //
        pairNumber.Anchor = AnchorStyles.Right;
        pairNumber.AutoSize = true;
        pairNumber.Name = "pairNumber";
        pairNumber.Size = new Size(0, 15);
        pairNumber.TabIndex = 1;
        //
        // bar
        //
        bar.Dock = DockStyle.Fill;
        bar.Maximum = 1;
        bar.Name = "bar";
        bar.Size = new Size(794, 22);
        bar.TabIndex = 1;
        //
        // counters
        //
        counters.AutoSize = true;
        counters.Name = "counters";
        counters.Size = new Size(358, 15);
        counters.TabIndex = 2;
        counters.Text = "Сохранено 0 │ Пропущено 0 │ Скорость 0 фото/с │ Осталось —";
        //
        // logPanel
        //
        logPanel.Controls.Add(log);
        logPanel.Dock = DockStyle.Fill;
        logPanel.Name = "logPanel";
        logPanel.Padding = new Padding(10, 0, 10, 6);
        logPanel.Size = new Size(820, 223);
        logPanel.TabIndex = 2;
        //
        // log
        //
        log.Dock = DockStyle.Fill;
        log.Multiline = true;
        log.Name = "log";
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.Size = new Size(800, 217);
        log.TabIndex = 0;
        //
        // buttons
        //
        buttons.AutoSize = true;
        buttons.Controls.Add(start);
        buttons.Controls.Add(stop);
        buttons.Dock = DockStyle.Bottom;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Name = "buttons";
        buttons.Padding = new Padding(10, 6, 10, 10);
        buttons.Size = new Size(820, 45);
        buttons.TabIndex = 3;
        //
        // start
        //
        start.Name = "start";
        start.Size = new Size(90, 23);
        start.TabIndex = 0;
        start.Text = "Старт";
        //
        // stop
        //
        stop.Enabled = false;
        stop.Name = "stop";
        stop.Size = new Size(90, 23);
        stop.TabIndex = 1;
        stop.Text = "Стоп";
        //
        // MainForm
        //
        ClientSize = new Size(820, 640);
        Controls.Add(logPanel);
        Controls.Add(state);
        Controls.Add(fields);
        Controls.Add(buttons);
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(700, 560);
        Name = "MainForm";
        Text = "FaceExport — экспорт лиц для обучения";
        fields.ResumeLayout(false);
        fields.PerformLayout();
        ageRow.ResumeLayout(false);
        ageRow.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)ageFrom).EndInit();
        ((System.ComponentModel.ISupportInitialize)ageTo).EndInit();
        state.ResumeLayout(false);
        state.PerformLayout();
        head.ResumeLayout(false);
        head.PerformLayout();
        logPanel.ResumeLayout(false);
        logPanel.PerformLayout();
        buttons.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }

    private TableLayoutPanel fields;
    private Label labelSql;
    private TextBox sql;
    private Label labelAge;
    private FlowLayoutPanel ageRow;
    private Label labelFrom;
    private NumericUpDown ageFrom;
    private Label labelTo;
    private NumericUpDown ageTo;
    private Label labelSexes;
    private TextBox sexes;
    private Label labelFolder;
    private TextBox folder;
    private Button browse;
    private TableLayoutPanel state;
    private TableLayoutPanel head;
    private Label current;
    private Label pairNumber;
    private ProgressBar bar;
    private Label counters;
    private Panel logPanel;
    private TextBox log;
    private FlowLayoutPanel buttons;
    private Button start;
    private Button stop;
}
