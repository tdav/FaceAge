partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    private System.Windows.Forms.TableLayoutPanel fields;
    private System.Windows.Forms.Label labelServer;
    private System.Windows.Forms.TextBox server;
    private System.Windows.Forms.Label labelUser;
    private System.Windows.Forms.TextBox user;
    private System.Windows.Forms.TextBox password;
    private System.Windows.Forms.Label labelSql;
    private System.Windows.Forms.TextBox sql;
    private System.Windows.Forms.Label labelAge;
    private System.Windows.Forms.FlowLayoutPanel ageRow;
    private System.Windows.Forms.Label labelFrom;
    private System.Windows.Forms.NumericUpDown ageFrom;
    private System.Windows.Forms.Label labelTo;
    private System.Windows.Forms.NumericUpDown ageTo;
    private System.Windows.Forms.Label labelSexes;
    private System.Windows.Forms.TextBox sexes;
    private System.Windows.Forms.Label labelFolder;
    private System.Windows.Forms.TextBox folder;
    private System.Windows.Forms.Button browse;
    private System.Windows.Forms.TableLayoutPanel state;
    private System.Windows.Forms.TableLayoutPanel head;
    private System.Windows.Forms.Label current;
    private System.Windows.Forms.Label pairNumber;
    private System.Windows.Forms.ProgressBar bar;
    private System.Windows.Forms.Label counters;
    private System.Windows.Forms.Panel logPanel;
    private System.Windows.Forms.TextBox log;
    private System.Windows.Forms.FlowLayoutPanel buttons;
    private System.Windows.Forms.Button start;
    private System.Windows.Forms.Button stop;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (this.components != null))
        {
            this.components.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Раскладка окна. Метод правит конструктор форм Visual Studio; макет — раздел 5 описания проекта.</summary>
    private void InitializeComponent()
    {
        this.fields = new System.Windows.Forms.TableLayoutPanel();
        this.labelServer = new System.Windows.Forms.Label();
        this.server = new System.Windows.Forms.TextBox();
        this.labelUser = new System.Windows.Forms.Label();
        this.user = new System.Windows.Forms.TextBox();
        this.password = new System.Windows.Forms.TextBox();
        this.labelSql = new System.Windows.Forms.Label();
        this.sql = new System.Windows.Forms.TextBox();
        this.labelAge = new System.Windows.Forms.Label();
        this.ageRow = new System.Windows.Forms.FlowLayoutPanel();
        this.labelFrom = new System.Windows.Forms.Label();
        this.ageFrom = new System.Windows.Forms.NumericUpDown();
        this.labelTo = new System.Windows.Forms.Label();
        this.ageTo = new System.Windows.Forms.NumericUpDown();
        this.labelSexes = new System.Windows.Forms.Label();
        this.sexes = new System.Windows.Forms.TextBox();
        this.labelFolder = new System.Windows.Forms.Label();
        this.folder = new System.Windows.Forms.TextBox();
        this.browse = new System.Windows.Forms.Button();
        this.state = new System.Windows.Forms.TableLayoutPanel();
        this.head = new System.Windows.Forms.TableLayoutPanel();
        this.current = new System.Windows.Forms.Label();
        this.pairNumber = new System.Windows.Forms.Label();
        this.bar = new System.Windows.Forms.ProgressBar();
        this.counters = new System.Windows.Forms.Label();
        this.logPanel = new System.Windows.Forms.Panel();
        this.log = new System.Windows.Forms.TextBox();
        this.buttons = new System.Windows.Forms.FlowLayoutPanel();
        this.start = new System.Windows.Forms.Button();
        this.stop = new System.Windows.Forms.Button();
        this.fields.SuspendLayout();
        this.ageRow.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)(this.ageFrom)).BeginInit();
        ((System.ComponentModel.ISupportInitialize)(this.ageTo)).BeginInit();
        this.state.SuspendLayout();
        this.head.SuspendLayout();
        this.logPanel.SuspendLayout();
        this.buttons.SuspendLayout();
        this.SuspendLayout();
        //
        // fields
        //
        this.fields.AutoSize = true;
        this.fields.ColumnCount = 4;
        this.fields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 150F));
        this.fields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this.fields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 170F));
        this.fields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 90F));
        this.fields.Controls.Add(this.labelServer, 0, 0);
        this.fields.Controls.Add(this.server, 1, 0);
        this.fields.Controls.Add(this.labelUser, 0, 1);
        this.fields.Controls.Add(this.user, 1, 1);
        this.fields.Controls.Add(this.password, 2, 1);
        this.fields.Controls.Add(this.labelSql, 0, 2);
        this.fields.Controls.Add(this.sql, 1, 2);
        this.fields.Controls.Add(this.labelAge, 0, 3);
        this.fields.Controls.Add(this.ageRow, 1, 3);
        this.fields.Controls.Add(this.labelFolder, 0, 4);
        this.fields.Controls.Add(this.folder, 1, 4);
        this.fields.Controls.Add(this.browse, 3, 4);
        this.fields.Dock = System.Windows.Forms.DockStyle.Top;
        this.fields.Name = "fields";
        this.fields.Padding = new System.Windows.Forms.Padding(10, 10, 10, 4);
        //
        // labelServer
        //
        this.labelServer.AutoSize = true;
        this.labelServer.Margin = new System.Windows.Forms.Padding(0, 6, 0, 0);
        this.labelServer.Name = "labelServer";
        this.labelServer.Text = "Сервер Oracle";
        //
        // server
        //
        this.fields.SetColumnSpan(this.server, 3);
        this.server.Dock = System.Windows.Forms.DockStyle.Fill;
        this.server.Name = "server";
        //
        // labelUser
        //
        this.labelUser.AutoSize = true;
        this.labelUser.Margin = new System.Windows.Forms.Padding(0, 6, 0, 0);
        this.labelUser.Name = "labelUser";
        this.labelUser.Text = "Пользователь, пароль";
        //
        // user
        //
        this.user.Dock = System.Windows.Forms.DockStyle.Fill;
        this.user.Name = "user";
        //
        // password
        //
        this.fields.SetColumnSpan(this.password, 2);
        this.password.Dock = System.Windows.Forms.DockStyle.Fill;
        this.password.Name = "password";
        this.password.UseSystemPasswordChar = true;
        //
        // labelSql
        //
        this.labelSql.AutoSize = true;
        this.labelSql.Margin = new System.Windows.Forms.Padding(0, 6, 0, 0);
        this.labelSql.Name = "labelSql";
        this.labelSql.Text = "SQL-запрос";
        //
        // sql
        //
        this.sql.AcceptsReturn = true;
        this.fields.SetColumnSpan(this.sql, 3);
        this.sql.Dock = System.Windows.Forms.DockStyle.Fill;
        this.sql.Font = new System.Drawing.Font("Courier New", 9.5F);
        this.sql.Multiline = true;
        this.sql.Name = "sql";
        this.sql.ScrollBars = System.Windows.Forms.ScrollBars.Both;
        this.sql.Size = new System.Drawing.Size(100, 110);
        this.sql.WordWrap = false;
        //
        // labelAge
        //
        this.labelAge.AutoSize = true;
        this.labelAge.Margin = new System.Windows.Forms.Padding(0, 6, 0, 0);
        this.labelAge.Name = "labelAge";
        this.labelAge.Text = "Возраст и пол";
        //
        // ageRow
        //
        this.ageRow.AutoSize = true;
        this.fields.SetColumnSpan(this.ageRow, 3);
        this.ageRow.Controls.Add(this.labelFrom);
        this.ageRow.Controls.Add(this.ageFrom);
        this.ageRow.Controls.Add(this.labelTo);
        this.ageRow.Controls.Add(this.ageTo);
        this.ageRow.Controls.Add(this.labelSexes);
        this.ageRow.Controls.Add(this.sexes);
        this.ageRow.Dock = System.Windows.Forms.DockStyle.Fill;
        this.ageRow.Margin = new System.Windows.Forms.Padding(0);
        this.ageRow.Name = "ageRow";
        this.ageRow.WrapContents = false;
        //
        // labelFrom
        //
        this.labelFrom.AutoSize = true;
        this.labelFrom.Margin = new System.Windows.Forms.Padding(0, 6, 3, 0);
        this.labelFrom.Name = "labelFrom";
        this.labelFrom.Text = "от";
        //
        // ageFrom
        //
        this.ageFrom.Maximum = new decimal(new int[] { 120, 0, 0, 0 });
        this.ageFrom.Name = "ageFrom";
        this.ageFrom.Size = new System.Drawing.Size(60, 23);
        this.ageFrom.Value = new decimal(new int[] { 10, 0, 0, 0 });
        //
        // labelTo
        //
        this.labelTo.AutoSize = true;
        this.labelTo.Margin = new System.Windows.Forms.Padding(10, 6, 3, 0);
        this.labelTo.Name = "labelTo";
        this.labelTo.Text = "до";
        //
        // ageTo
        //
        this.ageTo.Maximum = new decimal(new int[] { 120, 0, 0, 0 });
        this.ageTo.Name = "ageTo";
        this.ageTo.Size = new System.Drawing.Size(60, 23);
        this.ageTo.Value = new decimal(new int[] { 55, 0, 0, 0 });
        //
        // labelSexes
        //
        this.labelSexes.AutoSize = true;
        this.labelSexes.Margin = new System.Windows.Forms.Padding(16, 6, 3, 0);
        this.labelSexes.Name = "labelSexes";
        this.labelSexes.Text = "значения :sex";
        //
        // sexes
        //
        this.sexes.Name = "sexes";
        this.sexes.Size = new System.Drawing.Size(90, 23);
        this.sexes.Text = "1;2";
        //
        // labelFolder
        //
        this.labelFolder.AutoSize = true;
        this.labelFolder.Margin = new System.Windows.Forms.Padding(0, 6, 0, 0);
        this.labelFolder.Name = "labelFolder";
        this.labelFolder.Text = "Папка экспорта";
        //
        // folder
        //
        this.fields.SetColumnSpan(this.folder, 2);
        this.folder.Dock = System.Windows.Forms.DockStyle.Fill;
        this.folder.Name = "folder";
        //
        // browse
        //
        this.browse.Dock = System.Windows.Forms.DockStyle.Fill;
        this.browse.Name = "browse";
        this.browse.Text = "Обзор";
        //
        // state
        //
        this.state.AutoSize = true;
        this.state.ColumnCount = 1;
        this.state.Controls.Add(this.head, 0, 0);
        this.state.Controls.Add(this.bar, 0, 1);
        this.state.Controls.Add(this.counters, 0, 2);
        this.state.Dock = System.Windows.Forms.DockStyle.Top;
        this.state.Name = "state";
        this.state.Padding = new System.Windows.Forms.Padding(10, 6, 10, 4);
        //
        // head
        //
        this.head.AutoSize = true;
        this.head.ColumnCount = 2;
        this.head.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this.head.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
        this.head.Controls.Add(this.current, 0, 0);
        this.head.Controls.Add(this.pairNumber, 1, 0);
        this.head.Dock = System.Windows.Forms.DockStyle.Fill;
        this.head.Name = "head";
        //
        // current
        //
        this.current.AutoSize = true;
        this.current.Name = "current";
        this.current.Text = "Ожидание запуска";
        //
        // pairNumber
        //
        this.pairNumber.Anchor = System.Windows.Forms.AnchorStyles.Right;
        this.pairNumber.AutoSize = true;
        this.pairNumber.Name = "pairNumber";
        //
        // bar
        //
        this.bar.Dock = System.Windows.Forms.DockStyle.Fill;
        this.bar.Maximum = 1;
        this.bar.Name = "bar";
        this.bar.Size = new System.Drawing.Size(100, 22);
        //
        // counters
        //
        this.counters.AutoSize = true;
        this.counters.Name = "counters";
        this.counters.Text = "Сохранено 0 │ Пропущено 0 │ Скорость 0 фото/с │ Осталось —";
        //
        // logPanel
        //
        this.logPanel.Controls.Add(this.log);
        this.logPanel.Dock = System.Windows.Forms.DockStyle.Fill;
        this.logPanel.Name = "logPanel";
        this.logPanel.Padding = new System.Windows.Forms.Padding(10, 0, 10, 6);
        //
        // log
        //
        this.log.Dock = System.Windows.Forms.DockStyle.Fill;
        this.log.Multiline = true;
        this.log.Name = "log";
        this.log.ReadOnly = true;
        this.log.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
        //
        // buttons
        //
        this.buttons.AutoSize = true;
        this.buttons.Controls.Add(this.start);
        this.buttons.Controls.Add(this.stop);
        this.buttons.Dock = System.Windows.Forms.DockStyle.Bottom;
        this.buttons.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
        this.buttons.Name = "buttons";
        this.buttons.Padding = new System.Windows.Forms.Padding(10, 6, 10, 10);
        //
        // start
        //
        this.start.Name = "start";
        this.start.Size = new System.Drawing.Size(90, 23);
        this.start.Text = "Старт";
        //
        // stop
        //
        this.stop.Enabled = false;
        this.stop.Name = "stop";
        this.stop.Size = new System.Drawing.Size(90, 23);
        this.stop.Text = "Стоп";
        //
        // MainForm
        //
        this.ClientSize = new System.Drawing.Size(820, 640);
        this.Controls.Add(this.logPanel);
        this.Controls.Add(this.state);
        this.Controls.Add(this.fields);
        this.Controls.Add(this.buttons);
        this.Font = new System.Drawing.Font("Segoe UI", 9F);
        this.MinimumSize = new System.Drawing.Size(700, 560);
        this.Name = "MainForm";
        this.Text = "FaceExport — экспорт лиц для обучения";
        this.fields.ResumeLayout(false);
        this.fields.PerformLayout();
        this.ageRow.ResumeLayout(false);
        this.ageRow.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)(this.ageFrom)).EndInit();
        ((System.ComponentModel.ISupportInitialize)(this.ageTo)).EndInit();
        this.state.ResumeLayout(false);
        this.state.PerformLayout();
        this.head.ResumeLayout(false);
        this.head.PerformLayout();
        this.logPanel.ResumeLayout(false);
        this.logPanel.PerformLayout();
        this.buttons.ResumeLayout(false);
        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
