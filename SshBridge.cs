using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet;

namespace SshBridge
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SshBridgeForm());
        }
    }

    public class SshBridgeForm : Form
    {
        private RichTextBox _outputBox = null!;
        private TextBox _hostBox = null!;
        private TextBox _portBox = null!;
        private TextBox _userBox = null!;
        private TextBox _passwordBox = null!;
        private Button _connectButton = null!;
        private Button _disconnectButton = null!;
        private Label _statusLabel = null!;
        private Panel _loginPanel = null!;
        private Panel _sessionPanel = null!;

        private Thread? _serverThread;
        private CancellationTokenSource? _cts;
        private SshClient? _sshClient;
        private bool _isConnected;
        private int _commandCount;
        private string _currentHost = "";
        private string _currentUser = "";

        private const int PORT = 52718;

        public SshBridgeForm()
        {
            InitializeComponents();
            StartTcpServer();
        }

        private void InitializeComponents()
        {
            this.Text = "SSH Bridge for Claude";
            this.Size = new Size(700, 500);
            this.MinimumSize = new Size(500, 400);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;

            // Login Panel
            _loginPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
            };

            var loginLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(50, 30, 50, 30),
            };
            loginLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            loginLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            loginLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            loginLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            loginLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            loginLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            loginLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            loginLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            loginLayout.Controls.Add(new Label { Text = "Host:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
            _hostBox = new TextBox { Dock = DockStyle.Fill, Text = "" };
            loginLayout.Controls.Add(_hostBox, 1, 0);

            loginLayout.Controls.Add(new Label { Text = "Port:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
            _portBox = new TextBox { Dock = DockStyle.Fill, Text = "22", Width = 80 };
            loginLayout.Controls.Add(_portBox, 1, 1);

            loginLayout.Controls.Add(new Label { Text = "User:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
            _userBox = new TextBox { Dock = DockStyle.Fill, Text = "" };
            loginLayout.Controls.Add(_userBox, 1, 2);

            loginLayout.Controls.Add(new Label { Text = "Password:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
            _passwordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            _passwordBox.KeyPress += (s, e) => { if (e.KeyChar == (char)Keys.Enter) Connect(); };
            loginLayout.Controls.Add(_passwordBox, 1, 3);

            _connectButton = new Button { Text = "Connect", Width = 100, Height = 35 };
            _connectButton.Click += (s, e) => Connect();
            var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            buttonPanel.Controls.Add(_connectButton);
            loginLayout.Controls.Add(buttonPanel, 1, 4);

            _loginPanel.Controls.Add(loginLayout);

            // Session Panel
            _sessionPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Visible = false,
            };

            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(45, 45, 48),
                Padding = new Padding(10, 5, 10, 5),
            };

            _statusLabel = new Label
            {
                Text = "Disconnected",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 10),
                Font = new Font("Segoe UI", 10),
            };
            topBar.Controls.Add(_statusLabel);

            _disconnectButton = new Button
            {
                Text = "Disconnect",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(180, 50, 50),
                FlatStyle = FlatStyle.Flat,
                Width = 90,
                Height = 28,
                Anchor = AnchorStyles.Right,
            };
            _disconnectButton.Location = new Point(topBar.Width - _disconnectButton.Width - 10, 6);
            _disconnectButton.Click += (s, e) => Disconnect();
            topBar.Controls.Add(_disconnectButton);
            topBar.Resize += (s, e) => _disconnectButton.Location = new Point(topBar.Width - _disconnectButton.Width - 10, 6);

            _outputBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 10),
                WordWrap = false,
            };

            _sessionPanel.Controls.Add(_outputBox);
            _sessionPanel.Controls.Add(topBar);

            this.Controls.Add(_loginPanel);
            this.Controls.Add(_sessionPanel);
        }

        private void Connect()
        {
            string host = _hostBox.Text.Trim();
            string user = _userBox.Text.Trim();
            string password = _passwordBox.Text;
            int port = 22;
            int.TryParse(_portBox.Text.Trim(), out port);
            if (port <= 0 || port > 65535) port = 22;

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("Please fill in all fields.", "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _connectButton.Enabled = false;
            _connectButton.Text = "Connecting...";

            Task.Run(() =>
            {
                try
                {
                    _sshClient = new SshClient(host, port, user, password);
                    _sshClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
                    _sshClient.Connect();

                    if (_sshClient.IsConnected)
                    {
                        _currentHost = host;
                        _currentUser = user;
                        _commandCount = 0;

                        this.Invoke(() =>
                        {
                            _passwordBox.Clear();
                            OnConnected();
                        });
                    }
                    else
                    {
                        throw new Exception("Connection failed");
                    }
                }
                catch (Exception ex)
                {
                    this.Invoke(() =>
                    {
                        MessageBox.Show($"Connection failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _connectButton.Enabled = true;
                        _connectButton.Text = "Connect";
                    });
                }
            });
        }

        private void OnConnected()
        {
            _isConnected = true;
            _loginPanel.Visible = false;
            _sessionPanel.Visible = true;
            _statusLabel.Text = $"Connected to {_currentUser}@{_currentHost}";
            _outputBox.Clear();
            AppendOutput($"=== Connected to {_currentUser}@{_currentHost} ===", Color.LimeGreen);
        }

        private void Disconnect()
        {
            _isConnected = false;

            try
            {
                _sshClient?.Disconnect();
                _sshClient?.Dispose();
            }
            catch { }

            _sshClient = null;
            _currentHost = "";
            _currentUser = "";

            _loginPanel.Visible = true;
            _sessionPanel.Visible = false;
            _connectButton.Enabled = true;
            _connectButton.Text = "Connect";
            _statusLabel.Text = "Disconnected";
        }

        private void AppendOutput(string? text, Color? color = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            if (_outputBox.InvokeRequired)
            {
                _outputBox.Invoke(() => AppendOutput(text, color));
                return;
            }

            // Normalize line endings to Windows style
            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            
            _outputBox.SelectionStart = _outputBox.TextLength;
            _outputBox.SelectionLength = 0;
            _outputBox.SelectionColor = color ?? Color.FromArgb(220, 220, 220);
            _outputBox.AppendText(normalized + "\r\n");
            _outputBox.SelectionColor = _outputBox.ForeColor;
            _outputBox.ScrollToCaret();
        }

        public string ExecuteCommand(string command)
        {
            if (!_isConnected || _sshClient == null || !_sshClient.IsConnected)
            {
                return "ERROR: Not connected. Open SSH Bridge and connect first.";
            }

            try
            {
                _commandCount++;
                this.Invoke(() => _statusLabel.Text = $"Connected to {_currentUser}@{_currentHost} ({_commandCount} commands)");
                
                AppendOutput($"> {command}", Color.Cyan);
                
                using var cmd = _sshClient.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                var result = cmd.Execute();
                var error = cmd.Error;

                var output = result.Trim();
                if (!string.IsNullOrEmpty(error))
                {
                    output += "\n[stderr] " + error.Trim();
                }

                if (string.IsNullOrEmpty(output))
                {
                    output = "(no output)";
                }

                AppendOutput(output);
                
                return output;
            }
            catch (Exception ex)
            {
                var error = $"ERROR: {ex.Message}";
                AppendOutput(error, Color.Red);
                return error;
            }
        }

        private void StartTcpServer()
        {
            _cts = new CancellationTokenSource();
            _serverThread = new Thread(() => TcpServerLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "SshBridge TCP Server"
            };
            _serverThread.Start();
        }

        private void TcpServerLoop(CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Loopback, PORT);
            listener.Start();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!listener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    using var client = listener.AcceptTcpClient();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                    string? command = reader.ReadLine();
                    if (!string.IsNullOrEmpty(command))
                    {
                        string response;
                        if (command == "__STATUS__")
                        {
                            response = _isConnected ? $"CONNECTED:{_currentUser}@{_currentHost}" : "DISCONNECTED";
                        }
                        else if (command.StartsWith("__PREFILL__:"))
                        {
                            // Format: __PREFILL__:host:port:user:password
                            // Password is optional - if not provided, user must enter it
                            var parts = command.Substring(12).Split(':', 4);
                            if (parts.Length >= 3)
                            {
                                this.Invoke(() =>
                                {
                                    _hostBox.Text = parts[0];
                                    _portBox.Text = parts[1];
                                    _userBox.Text = parts[2];
                                    if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]))
                                    {
                                        _passwordBox.Text = parts[3];
                                    }
                                    _passwordBox.Focus();
                                    this.Activate();
                                    this.BringToFront();
                                });
                                response = "PREFILLED";
                            }
                            else
                            {
                                response = "ERROR: Invalid prefill format. Use __PREFILL__:host:port:user[:password]";
                            }
                        }
                        else if (command == "__CONNECT__")
                        {
                            // Trigger connect if fields are filled
                            this.Invoke(() => Connect());
                            response = "CONNECTING";
                        }
                        else
                        {
                            response = ExecuteCommand(command);
                        }
                        // Encode newlines so they survive the single-line protocol
                        response = response.Replace("\r\n", "<<CRLF>>").Replace("\n", "<<LF>>");
                        writer.WriteLine(response);
                    }
                }
                catch (Exception)
                {
                    // Ignore errors
                }
            }

            listener.Stop();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            Disconnect();
            base.OnFormClosing(e);
        }
    }
}
