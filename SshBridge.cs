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
        private bool _allowSudo;
        private bool _isRunning;
        private string _storedPassword = "";
        private Button _stayOnTopButton = null!;
        private Button _penButton = null!;
        private Button _sudoButton = null!;
        private System.Windows.Forms.Timer _runningTimer = null!;
        private int _runningDots;

        private const int PORT = 52718;
        private const int MAX_OUTPUT_BYTES = 500 * 1024; // 500KB limit for MCP
        private const int MAX_OUTPUT_LINES = 150; // Keep last N lines for large output
        private const int DEFAULT_TIMEOUT_MS = 30000;
        
        private int _nextCommandTimeoutMs = DEFAULT_TIMEOUT_MS; // Configurable per-command
        private Dictionary<string, int> _spawnedProcesses = new(); // Track background processes

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

            _sudoButton = new Button
            {
                Text = "🔓 Sudo",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(80, 80, 85),
                FlatStyle = FlatStyle.Flat,
                Width = 80,
                Height = 28,
            };
            _sudoButton.Click += (s, e) => ToggleSudo();
            topBar.Controls.Add(_sudoButton);

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

            // Right-click context menu
            var contextMenu = new ContextMenuStrip();
            var copyItem = new ToolStripMenuItem("Copy", null, (s, e) =>
            {
                if (_outputBox.SelectionLength > 0)
                    Clipboard.SetText(_outputBox.SelectedText);
            });
            var copyAllItem = new ToolStripMenuItem("Copy All", null, (s, e) =>
            {
                if (!string.IsNullOrEmpty(_outputBox.Text))
                    Clipboard.SetText(_outputBox.Text);
            });
            var clearItem = new ToolStripMenuItem("Clear", null, (s, e) =>
            {
                _outputBox.Clear();
            });
            contextMenu.Items.Add(copyItem);
            contextMenu.Items.Add(copyAllItem);
            contextMenu.Items.AddRange(new ToolStripItem[] { new ToolStripSeparator(), clearItem });
            _outputBox.ContextMenuStrip = contextMenu;

            _sessionPanel.Controls.Add(_outputBox);
            _sessionPanel.Controls.Add(topBar);

            this.Controls.Add(_loginPanel);
            this.Controls.Add(_sessionPanel);

            // Timer for running indicator
            _runningTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _runningTimer.Tick += (s, e) =>
            {
                if (_isRunning)
                {
                    _runningDots = (_runningDots + 1) % 4;
                    var dots = new string('.', _runningDots);
                    _statusLabel.Text = $"⚡ Running{dots}";
                    _statusLabel.ForeColor = Color.Yellow;
                }
            };
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
                            _storedPassword = _passwordBox.Text;
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
            _allowSudo = false;
            _storedPassword = "";

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
            UpdateSudoButton();
        }

        private void RepositionTopBarButtons()
        {
            int rightEdge = _sessionPanel.Width - 10;
            _disconnectButton.Location = new Point(rightEdge - _disconnectButton.Width, 6);
            _penButton.Location = new Point(_disconnectButton.Left - _penButton.Width - 5, 6);
            _stayOnTopButton.Location = new Point(_penButton.Left - _stayOnTopButton.Width - 5, 6);
            _sudoButton.Location = new Point(_stayOnTopButton.Left - _sudoButton.Width - 5, 6);
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

        private void ToggleSudo()
        {
            if (string.IsNullOrEmpty(_storedPassword))
            {
                MessageBox.Show("No password stored. Reconnect to enable sudo.", "Sudo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            _allowSudo = !_allowSudo;
            UpdateSudoButton();
            
            if (_allowSudo)
            {
                AppendOutput("=== SUDO ENABLED - Password will auto-send ===", Color.FromArgb(180, 100, 255));
            }
            else
            {
                AppendOutput("=== SUDO DISABLED ===", Color.Gray);
            }
        }

        private void UpdateSudoButton()
        {
            _sudoButton.BackColor = _allowSudo 
                ? Color.FromArgb(140, 80, 200)  // Purple when enabled
                : Color.FromArgb(80, 80, 85);   // Gray when disabled
            _sudoButton.Text = _allowSudo ? "🔐 Sudo On" : "🔓 Sudo";
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
                    SetRunning(true);
                    
                    // Clear any pending output
                    ReadAvailable();
                    
                    // Block interactive commands that would break the shell
                    if (IsBlockedCommand(command, out string blockReason))
                    {
                        AppendOutput($"> {command}", Color.Gray);
                        AppendOutput($"[BLOCKED - Interactive command]\n{blockReason}", Color.Red);
                        return $"BLOCKED: Interactive command not supported.\n{blockReason}";
                    }
                    
                    // Block sudo commands if sudo not enabled - BEFORE sending
                    if (command.TrimStart().StartsWith("sudo") && !_allowSudo)
                    {
                        AppendOutput($"> {command}", Color.White);
                        AppendOutput("[SUDO BLOCKED - Command not sent. Enable sudo button to allow.]", Color.Red);
                        return "SUDO_BLOCKED: Sudo commands are disabled. Click the Sudo button to enable.";
                    }
                    
                    // Send command
                    _shellStream.WriteLine(command);
                    
                    // Wait for output and prompt
                    var output = WaitForPrompt(_nextCommandTimeoutMs);
                    _nextCommandTimeoutMs = DEFAULT_TIMEOUT_MS; // Reset after use
                    
                    // Handle sudo password prompt (only runs if sudo IS enabled)
                    if (command.TrimStart().StartsWith("sudo") && IsSudoPrompt(output))
                    {
                        AppendOutput("[sudo password auto-sent]", Color.FromArgb(180, 100, 255));
                        _shellStream.WriteLine(_storedPassword);
                        var sudoOutput = WaitForPrompt(30000);
                        output += sudoOutput;
                    }

                    
                    // Clean up output - remove the echoed command and prompt
                    output = CleanOutput(output, command);
                    
                    if (string.IsNullOrEmpty(output))
                    {
                        output = "(no output)";
                    }

                    // Tail large output - keep last N lines
                    var lines = output.Split('\n');
                    string returnOutput;
                    if (lines.Length > MAX_OUTPUT_LINES)
                    {
                        var tailLines = lines.Skip(lines.Length - MAX_OUTPUT_LINES).ToArray();
                        returnOutput = $"[... {lines.Length - MAX_OUTPUT_LINES} lines truncated ...]\n" + string.Join("\n", tailLines);
                    }
                    else
                    {
                        returnOutput = output;
                    }
                    
                    // Output already displayed in real-time by WaitForPrompt
                    if (lines.Length > MAX_OUTPUT_LINES)
                    {
                        AppendOutput($"[Returned last {MAX_OUTPUT_LINES} of {lines.Length} lines to Claude]", Color.Gray);
                    }
                    
                    // Final size check
                    if (Encoding.UTF8.GetByteCount(returnOutput) > MAX_OUTPUT_BYTES)
                    {
                        returnOutput = TruncateToBytes(returnOutput, MAX_OUTPUT_BYTES);
                        returnOutput += $"\n\n[OUTPUT TRUNCATED - exceeded 500KB limit]";
                    }
                    
                    return returnOutput;
                }
                catch (Exception ex)
                {
                    var error = $"ERROR: {ex.Message}";
                    AppendOutput(error, Color.Red);
                    return error;
                }
                finally
                {
                    SetRunning(false);
                }
            }
        }

        private void SetRunning(bool running)
        {
            _isRunning = running;
            if (this.InvokeRequired)
            {
                this.Invoke(() => SetRunning(running));
                return;
            }
            
            if (running)
            {
                _runningDots = 0;
                _runningTimer.Start();
            }
            else
            {
                _runningTimer.Stop();
                _statusLabel.Text = $"Connected to {_currentUser}@{_currentHost}";
                _statusLabel.ForeColor = Color.White;
            }
        }

        private static string TruncateToBytes(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= maxBytes) return text;
            
            // Find a safe cut point (don't break UTF-8 sequences)
            int cutPoint = maxBytes;
            while (cutPoint > 0 && (bytes[cutPoint] & 0xC0) == 0x80)
                cutPoint--;
            
            return Encoding.UTF8.GetString(bytes, 0, cutPoint);
        }

        private bool IsProcessRunning(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                return !proc.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private void SendAbort()
        {
            if (_shellStream != null && _isConnected)
            {
                try
                {
                    _shellStream.Write("\x03"); // Ctrl+C
                    Thread.Sleep(100);
                    _shellStream.Write("\x03"); // Send twice for stubborn processes
                    AppendOutput("[ABORT SIGNAL SENT - Ctrl+C]", Color.Orange);
                }
                catch { }
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
            var lastDataTime = sw.ElapsedMilliseconds;
            
            // Minimum wait time to ensure we get all output
            const int minWaitMs = 300;
            const int quietTimeMs = 150;
            
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                // Check for pen-lift interrupt - abort immediately
                if (_penLifted)
                {
                    try { _shellStream.Write("\x03"); } catch { } // Send Ctrl+C
                    Thread.Sleep(200);
                    sb.Append(ReadAvailable()); // Grab remaining output
                    sb.Append("\n[ABORTED BY USER - Pen lifted]");
                    AppendOutput("[Command aborted - Pen lifted]", Color.Orange);
                    return sb.ToString();
                }
                
                if (_shellStream.DataAvailable)
                {
                    var buffer = new byte[4096];
                    var read = _shellStream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                        sb.Append(chunk);
                        lastDataTime = sw.ElapsedMilliseconds;
                        
                        // Real-time display update
                        var cleaned = StripAnsiCodes(chunk);
                        if (!string.IsNullOrEmpty(cleaned))
                        {
                            AppendOutput(cleaned, Color.FromArgb(180, 180, 180));
                        }
                    }
                }
                else
                {
                    // Only check for prompt after minimum wait AND data stops flowing
                    if (sw.ElapsedMilliseconds > minWaitMs && 
                        sw.ElapsedMilliseconds - lastDataTime > quietTimeMs)
                    {
                        var current = sb.ToString();
                        if (LooksLikePrompt(current))
                        {
                            break;
                        }
                    }
                    Thread.Sleep(5); // Fast polling for responsive terminal
                }
            }
            
            return sb.ToString();
        }

        private bool LooksLikePrompt(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            
            // Normalize line endings and get last non-empty line
            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');
            
            // Find last non-empty line
            string lastLine = "";
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var trimmed = lines[i].Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    lastLine = trimmed;
                    break;
                }
            }
            
            if (string.IsNullOrEmpty(lastLine)) return false;
            
            // Known prompt pattern from connection (most reliable)
            if (!string.IsNullOrEmpty(_promptPattern) && lastLine.Contains(_promptPattern)) return true;
            
            // Linux/Unix prompts: user@host patterns ending with $ or #
            if ((lastLine.EndsWith("$") || lastLine.EndsWith("#")) && lastLine.Contains("@")) return true;
            
            // Windows cmd prompt: ends with > and contains drive letter or path
            if (lastLine.EndsWith(">") && (lastLine.Contains(":\\") || lastLine.Contains(":/"))) return true;
            
            // PowerShell prompt
            if (lastLine.EndsWith("PS>") || lastLine.EndsWith("PS >")) return true;
            
            return false;
        }

        private bool IsSudoPrompt(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            
            // Only check the last line - prevents false matches on buffer history
            var lines = text.Split('\n');
            var lastLine = lines[lines.Length - 1].Trim().ToLowerInvariant();
            
            // Match password prompts
            if (lastLine.EndsWith("password:")) return true;
            if (lastLine.Contains("[sudo]") && lastLine.Contains("password")) return true;
            if (Regex.IsMatch(lastLine, @"password for \w+:")) return true;
            
            return false;
        }

        private bool IsBlockedCommand(string command, out string reason)
        {
            var trimmed = command.Trim();
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var firstWord = parts.FirstOrDefault()?.ToLowerInvariant() ?? "";
            var fullLower = trimmed.ToLowerInvariant();
            
            reason = "";
            
            switch (firstWord)
            {
                // Editors - always blocked (no non-interactive mode)
                case "nano":
                case "vim":
                case "vi":
                case "nvim":
                case "emacs":
                case "pico":
                case "joe":
                case "mcedit":
                    reason = "Interactive editors not supported. Use:\n" +
                             "• echo \"content\" > file.txt (create/overwrite)\n" +
                             "• echo \"more\" >> file.txt (append)\n" +
                             "• cat << 'EOF' > file.txt (multi-line)\n" +
                             "• sed -i 's/old/new/g' file.txt (find/replace)";
                    return true;
                    
                // Pagers - always blocked
                case "less":
                case "more":
                    reason = "Use 'cat', 'head -n 100', or 'tail -n 100' instead.";
                    return true;
                    
                // TUI monitors - allow batch mode
                case "top":
                    if (!fullLower.Contains("-b"))
                    {
                        reason = "Use 'top -b -n 1' for batch mode, or 'ps aux' instead.";
                        return true;
                    }
                    break;
                case "htop":
                case "btop":
                case "atop":
                case "nmon":
                case "glances":
                    reason = "Use 'ps aux', 'free -h', 'df -h', or 'top -b -n 1' instead.";
                    return true;
                    
                // Databases - allow with query flags
                case "mysql":
                    if (!fullLower.Contains("-e"))
                    {
                        reason = "Use 'mysql -e \"SELECT...\"' for non-interactive query.";
                        return true;
                    }
                    break;
                case "psql":
                    if (!fullLower.Contains("-c"))
                    {
                        reason = "Use 'psql -c \"SELECT...\"' for non-interactive query.";
                        return true;
                    }
                    break;
                case "mongo":
                case "mongosh":
                    if (!fullLower.Contains("--eval"))
                    {
                        reason = "Use 'mongosh --eval \"db.collection.find()\"' for non-interactive.";
                        return true;
                    }
                    break;
                case "redis-cli":
                    if (parts.Length == 1)
                    {
                        reason = "Add command: 'redis-cli GET key' or 'redis-cli INFO'.";
                        return true;
                    }
                    break;
                case "sqlite3":
                    if (!fullLower.Contains("-cmd") && !trimmed.Contains("\""))
                    {
                        reason = "Use 'sqlite3 db.sqlite \"SELECT...\"' for non-interactive.";
                        return true;
                    }
                    break;
                    
                // Terminal multiplexers - always blocked
                case "tmux":
                case "screen":
                case "byobu":
                    reason = "Terminal multiplexers not supported in this shell.";
                    return true;
                    
                // File managers - always blocked
                case "mc":
                case "ranger":
                case "nnn":
                    reason = "Use 'ls -la', 'find', or 'tree' instead.";
                    return true;
                    
                // Nested shells - block unless -c flag
                case "bash":
                case "zsh":
                case "fish":
                case "sh":
                case "csh":
                case "tcsh":
                    if (!fullLower.Contains("-c"))
                    {
                        reason = "Nested shells not supported. Use 'bash -c \"command\"' for one-offs.";
                        return true;
                    }
                    break;
                    
                // SSH/remote - always blocked
                case "ssh":
                case "telnet":
                    reason = "Nested SSH not supported. Disconnect and connect to the other server.";
                    return true;
                    
                // Man pages - always blocked
                case "man":
                case "info":
                    reason = "Use 'command --help' or search online.";
                    return true;
                    
                // FTP - always blocked
                case "ftp":
                case "sftp":
                    reason = "Interactive FTP not supported. Use 'scp' or 'curl' instead.";
                    return true;
            }
            
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
            
            // Strip ANSI escape codes and normalize line endings
            var cleaned = StripAnsiCodes(output);
            // Convert all line endings to \n only
            cleaned = cleaned.Replace("\r\n", "\n").Replace("\r", "\n");
            return cleaned.Trim();
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
                        else if (command == "__ABORT__")
                        {
                            SendAbort();
                            response = _isRunning ? "ABORT_SENT" : "NO_COMMAND_RUNNING";
                        }
                        else if (command == "__IS_RUNNING__")
                        {
                            response = _isRunning ? "RUNNING" : "IDLE";
                        }
                        else if (command.StartsWith("__TIMEOUT__:"))
                        {
                            var secStr = command.Substring(12);
                            if (int.TryParse(secStr, out int seconds) && seconds > 0 && seconds <= 3600)
                            {
                                _nextCommandTimeoutMs = seconds * 1000;
                                response = $"TIMEOUT_SET:{seconds}s";
                                AppendOutput($"[Timeout set to {seconds}s for next command]", Color.Cyan);
                            }
                            else
                            {
                                response = "ERROR: Invalid timeout (1-3600 seconds)";
                            }
                        }
                        else if (command.StartsWith("__KILL_PORT__:"))
                        {
                            var portStr = command.Substring(14);
                            if (int.TryParse(portStr, out int portNum) && portNum > 0 && portNum <= 65535)
                            {
                                // Execute netstat to find PID, then taskkill
                                var killCmd = $"for /f \"tokens=5\" %a in ('netstat -ano ^| findstr :{portNum} ^| findstr LISTENING') do @taskkill /PID %a /F 2>nul & echo Killed PID %a";
                                response = ExecuteCommand(killCmd);
                            }
                            else
                            {
                                response = "ERROR: Invalid port number";
                            }
                        }
                        else if (command.StartsWith("__WRITE_FILE__:"))
                        {
                            // Format: __WRITE_FILE__:C:\path\file.txt|content
                            // Uses | as delimiter since it's not valid in Windows paths
                            // Use <<LF>> for newlines, <<CR>> for carriage returns
                            var rest = command.Substring(15);
                            var pipeIdx = rest.IndexOf('|');
                            if (pipeIdx > 0)
                            {
                                var path = rest.Substring(0, pipeIdx);
                                var content = rest.Substring(pipeIdx + 1)
                                    .Replace("<<CRLF>>", "\r\n")
                                    .Replace("<<LF>>", "\r\n")   // Convert to Windows line endings
                                    .Replace("<<CR>>", "\r");
                                try
                                {
                                    // Base64 encode to avoid ALL escaping issues
                                    var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
                                    var psCmd = $"[System.IO.File]::WriteAllText('{path}', [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{base64}')))";
                                    ExecuteCommand($"powershell -Command \"{psCmd}\"");
                                    response = $"WRITTEN:{path} ({content.Length} chars)";
                                    AppendOutput($"[Wrote {content.Length} chars to {path}]", Color.Green);
                                }
                                catch (Exception ex)
                                {
                                    response = $"ERROR: {ex.Message}";
                                }
                            }
                            else
                            {
                                response = "ERROR: Use __WRITE_FILE__:path|content (use <<LF>> for newlines)";
                            }
                        }
                        else if (command.StartsWith("__APPEND_FILE__:"))
                        {
                            // Format: __APPEND_FILE__:C:\path\file.txt|content
                            var rest = command.Substring(16);
                            var pipeIdx = rest.IndexOf('|');
                            if (pipeIdx > 0)
                            {
                                var path = rest.Substring(0, pipeIdx);
                                var content = rest.Substring(pipeIdx + 1)
                                    .Replace("<<CRLF>>", "\r\n")
                                    .Replace("<<LF>>", "\r\n")
                                    .Replace("<<CR>>", "\r");
                                try
                                {
                                    // Base64 encode to avoid ALL escaping issues
                                    var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
                                    var psCmd = $"[System.IO.File]::AppendAllText('{path}', [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{base64}')))";
                                    ExecuteCommand($"powershell -Command \"{psCmd}\"");
                                    response = $"APPENDED:{path} ({content.Length} chars)";
                                    AppendOutput($"[Appended {content.Length} chars to {path}]", Color.Green);
                                }
                                catch (Exception ex)
                                {
                                    response = $"ERROR: {ex.Message}";
                                }
                            }
                            else
                            {
                                response = "ERROR: Use __APPEND_FILE__:path|content (use <<LF>> for newlines)";
                            }
                        }
                        else if (command.StartsWith("__SPAWN__:"))
                        {
                            // Format: __SPAWN__:name:command
                            var rest = command.Substring(10);
                            var colonIdx = rest.IndexOf(':');
                            if (colonIdx > 0)
                            {
                                var name = rest.Substring(0, colonIdx);
                                var spawnCmd = rest.Substring(colonIdx + 1);
                                // Run in background, capture PID via PowerShell
                                var psCmd = $"$p = Start-Process -FilePath cmd -ArgumentList '/c {spawnCmd.Replace("'", "''")}' -PassThru -WindowStyle Hidden; $p.Id";
                                var pidResult = ExecuteCommand($"powershell -Command \"{psCmd}\"");
                                if (int.TryParse(pidResult.Trim().Split('\n').Last().Trim(), out int pid))
                                {
                                    _spawnedProcesses[name] = pid;
                                    response = $"SPAWNED:{name}:PID={pid}";
                                }
                                else
                                {
                                    response = $"SPAWN_STARTED:{name} (PID unknown)";
                                }
                            }
                            else
                            {
                                response = "ERROR: Use __SPAWN__:name:command";
                            }
                        }
                        else if (command == "__LIST_SPAWNED__")
                        {
                            if (_spawnedProcesses.Count == 0)
                            {
                                response = "NO_SPAWNED_PROCESSES";
                            }
                            else
                            {
                                var sb = new StringBuilder();
                                foreach (var kvp in _spawnedProcesses)
                                {
                                    // Check if still running
                                    var checkCmd = $"tasklist /FI \"PID eq {kvp.Value}\" /NH 2>nul | findstr {kvp.Value}";
                                    var checkResult = ExecuteCommand(checkCmd);
                                    var status = checkResult.Contains(kvp.Value.ToString()) ? "RUNNING" : "STOPPED";
                                    sb.AppendLine($"{kvp.Key}: PID={kvp.Value} ({status})");
                                }
                                response = sb.ToString().TrimEnd();
                            }
                        }
                        else if (command == "__TAIL__")
                        {
                            // Get last 50 lines from the output textbox
                            string text = "";
                            this.Invoke(() => text = _outputBox.Text);
                            var lines = text.Split('\n');
                            var tail = lines.Skip(Math.Max(0, lines.Length - 50)).ToArray();
                            response = string.Join("\n", tail);
                        }
                        else if (command.StartsWith("__KILL_SPAWNED__:"))
                        {
                            var name = command.Substring(17);
                            if (_spawnedProcesses.TryGetValue(name, out int pid))
                            {
                                ExecuteCommand($"taskkill /PID {pid} /F /T 2>nul");
                                _spawnedProcesses.Remove(name);
                                response = $"KILLED:{name}:PID={pid}";
                            }
                            else
                            {
                                response = $"ERROR: No spawned process named '{name}'";
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
                        response = response.Replace("\r\n", "<<CRLF>>").Replace("\n", "<<LF>>").Replace("\r", "<<CR>>");
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
