using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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
        private ShellStream? _shellStream;
        private bool _isConnected;
        private int _commandCount;
        private string _currentHost = "";
        private string _currentUser = "";
        private string _promptPattern = "";
        private readonly object _shellLock = new object();
        
        // UI state
        private bool _stayOnTop;
        private bool _penLifted;
        private Button _stayOnTopButton = null!;
        private Button _penButton = null!;

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
            _disconnectButton.Click += (s, e) => Disconnect();
            topBar.Controls.Add(_disconnectButton);

            _penButton = new Button
            {
                Text = "✏️ Lift Pen",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(70, 130, 180),
                FlatStyle = FlatStyle.Flat,
                Width = 100,
                Height = 28,
            };
            _penButton.Click += (s, e) => TogglePen();
            topBar.Controls.Add(_penButton);

            _stayOnTopButton = new Button
            {
                Text = "📌 Pin",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(80, 80, 85),
                FlatStyle = FlatStyle.Flat,
                Width = 70,
                Height = 28,
            };
            _stayOnTopButton.Click += (s, e) => ToggleStayOnTop();
            topBar.Controls.Add(_stayOnTopButton);

            // Position buttons from right edge
            topBar.Resize += (s, e) => RepositionTopBarButtons();
            this.Load += (s, e) => RepositionTopBarButtons();

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

                        // Create persistent shell stream
                        _shellStream = _sshClient.CreateShellStream("xterm", 200, 50, 800, 600, 65536);
                        
                        // Wait for initial prompt and detect the pattern
                        Thread.Sleep(500);
                        var initial = ReadAvailable();
                        _promptPattern = DetectPrompt(initial, user);

                        this.Invoke(() =>
                        {
                            _passwordBox.Clear();
                            OnConnected();
                            if (!string.IsNullOrEmpty(initial))
                            {
                                var cleaned = StripAnsiCodes(initial).Trim();
                                if (!string.IsNullOrEmpty(cleaned))
                                {
                                    AppendOutput(cleaned, Color.Gray);
                                }
                            }
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
            _penLifted = false;

            try
            {
                _shellStream?.Dispose();
                _sshClient?.Disconnect();
                _sshClient?.Dispose();
            }
            catch { }

            _shellStream = null;
            _sshClient = null;
            _currentHost = "";
            _currentUser = "";
            _promptPattern = "";

            _loginPanel.Visible = true;
            _sessionPanel.Visible = false;
            _connectButton.Enabled = true;
            _connectButton.Text = "Connect";
            _statusLabel.Text = "Disconnected";
            UpdatePenButton();
        }

        private void RepositionTopBarButtons()
        {
            int rightEdge = _sessionPanel.Width - 10;
            _disconnectButton.Location = new Point(rightEdge - _disconnectButton.Width, 6);
            _penButton.Location = new Point(_disconnectButton.Left - _penButton.Width - 5, 6);
            _stayOnTopButton.Location = new Point(_penButton.Left - _stayOnTopButton.Width - 5, 6);
        }

        private void ToggleStayOnTop()
        {
            _stayOnTop = !_stayOnTop;
            this.TopMost = _stayOnTop;
            _stayOnTopButton.BackColor = _stayOnTop 
                ? Color.FromArgb(60, 140, 60)  // Green when active
                : Color.FromArgb(80, 80, 85);  // Gray when inactive
            _stayOnTopButton.Text = _stayOnTop ? "📌 Pinned" : "📌 Pin";
        }

        private void TogglePen()
        {
            _penLifted = !_penLifted;
            UpdatePenButton();
            
            if (_penLifted)
            {
                AppendOutput("=== PEN LIFTED - Claude paused ===", Color.Orange);
            }
            else
            {
                AppendOutput("=== PEN DOWN - Claude resumed ===", Color.LimeGreen);
            }
        }

        private void UpdatePenButton()
        {
            _penButton.BackColor = _penLifted 
                ? Color.FromArgb(200, 120, 50)  // Orange when lifted
                : Color.FromArgb(70, 130, 180); // Blue when down
            _penButton.Text = _penLifted ? "✏️ Pen Up!" : "✏️ Lift Pen";
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
            if (!_isConnected || _sshClient == null || !_sshClient.IsConnected || _shellStream == null)
            {
                return "ERROR: Not connected. Open SSH Bridge and connect first.";
            }

            lock (_shellLock)
            {
                try
                {
                    _commandCount++;
                    this.Invoke(() => _statusLabel.Text = $"Connected to {_currentUser}@{_currentHost} ({_commandCount} commands)");
                    
                    // Clear any pending output
                    ReadAvailable();
                    
                    // Send command
                    _shellStream.WriteLine(command);
                    
                    // Wait for output and prompt
                    var output = WaitForPrompt(30000);
                    
                    // Clean up output - remove the echoed command and prompt
                    output = CleanOutput(output, command);
                    
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
        }

        private string ReadAvailable()
        {
            if (_shellStream == null) return "";
            
            var sb = new StringBuilder();
            while (_shellStream.DataAvailable)
            {
                var buffer = new byte[4096];
                var read = _shellStream.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
            return sb.ToString();
        }

        private string WaitForPrompt(int timeoutMs)
        {
            if (_shellStream == null) return "";
            
            var sb = new StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_shellStream.DataAvailable)
                {
                    var buffer = new byte[4096];
                    var read = _shellStream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                        sb.Append(chunk);
                        
                        // Check if we've hit a prompt
                        var current = sb.ToString();
                        if (LooksLikePrompt(current))
                        {
                            break;
                        }
                    }
                }
                else
                {
                    Thread.Sleep(50);
                }
            }
            
            return sb.ToString();
        }

        private bool LooksLikePrompt(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            
            // Check for common prompt patterns at the end
            var trimmed = text.TrimEnd();
            
            // Windows cmd prompt: ends with >
            if (trimmed.EndsWith(">")) return true;
            
            // PowerShell prompt: ends with > or PS>
            if (trimmed.EndsWith("PS>") || trimmed.EndsWith("PS >")) return true;
            
            // Linux/Unix prompts: end with $ or #
            if (trimmed.EndsWith("$") || trimmed.EndsWith("#")) return true;
            
            // Known prompt pattern from connection
            if (!string.IsNullOrEmpty(_promptPattern) && trimmed.EndsWith(_promptPattern)) return true;
            
            return false;
        }

        private string DetectPrompt(string initialOutput, string user)
        {
            if (string.IsNullOrEmpty(initialOutput)) return ">";
            
            var lines = initialOutput.Split('\n');
            var lastLine = lines[^1].Trim();
            
            // Return the last line as the prompt pattern
            if (lastLine.EndsWith(">") || lastLine.EndsWith("$") || lastLine.EndsWith("#"))
            {
                return lastLine;
            }
            
            return ">";
        }

        private string CleanOutput(string output, string command)
        {
            if (string.IsNullOrEmpty(output)) return "";
            
            // Strip ANSI escape codes, keep everything else (prompts, commands, output)
            return StripAnsiCodes(output).Trim();
        }

        private static string StripAnsiCodes(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            // Remove ANSI escape sequences:
            // CSI sequences: ESC [ ... final_byte
            // OSC sequences: ESC ] ... BEL or ESC \
            // Simple escapes: ESC followed by single char
            
            var result = text;
            
            // CSI sequences (ESC [ or 0x9B followed by parameters and final byte)
            result = Regex.Replace(result, @"\x1B\[[0-9;?]*[A-Za-z]", "");
            result = Regex.Replace(result, @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");
            
            // OSC sequences (ESC ] ... BEL or ST)
            result = Regex.Replace(result, @"\x1B\][^\x07\x1B]*(\x07|\x1B\\)?", "");
            
            // Other escape sequences
            result = Regex.Replace(result, @"\x1B[()][AB012]", "");  // Character set selection
            result = Regex.Replace(result, @"\x1B[@-_]", "");        // C1 control codes
            
            // Clean up any remaining escape chars and control chars (except newline, tab, CR)
            result = Regex.Replace(result, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");
            
            return result;
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
                        else if (command == "__PEN_STATUS__")
                        {
                            response = _penLifted ? "PEN_LIFTED" : "PEN_DOWN";
                        }
                        else if (command == "__PEN_DOWN__")
                        {
                            if (_penLifted)
                            {
                                this.Invoke(() => TogglePen());
                                response = "PEN_LOWERED";
                            }
                            else
                            {
                                response = "PEN_ALREADY_DOWN";
                            }
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
                        else if (_penLifted)
                        {
                            // Block command execution when pen is lifted
                            AppendOutput($"> {command}", Color.Gray);
                            AppendOutput("[BLOCKED - Pen lifted by user]", Color.Orange);
                            response = "PEN_LIFTED: User has paused command execution. Use SshPenDown to resume, or wait for user to click 'Lift Pen' button again.";
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
