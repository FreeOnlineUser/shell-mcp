using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace ShellMcp
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var mode = Environment.GetEnvironmentVariable("SHELL_MCP_MODE") ?? "safe";
            ShellExecutor.Initialize(mode);

            var builder = Host.CreateApplicationBuilder(args);
            
            // CRITICAL: Disable ALL console logging for MCP stdio transport
            // Any stdout output will corrupt the JSON-RPC protocol and cause hangs
            builder.Logging.ClearProviders();
            // Optionally log to a file for debugging:
            // builder.Logging.AddFile("shell-mcp.log");
            
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();
            
            await builder.Build().RunAsync();
        }
    }

    // ===============================================
    // SHELL EXECUTOR
    // ===============================================

    public static class ShellExecutor
    {
        private static string _currentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private static string _mode = "safe";
        private static int _defaultTimeout = 30;

        private static readonly HashSet<string> SafeCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "dir", "ls", "pwd", "cd", "tree",
            "type", "cat", "head", "tail", "more", "less", "find", "findstr", "grep", "where", "which",
            "echo", "date", "time", "whoami", "hostname", "ver",
            "git status", "git log", "git diff", "git branch", "git remote", "git fetch", "git show", "git ls-files", "git stash list",
            "dotnet build", "dotnet run", "dotnet test", "dotnet restore", "dotnet clean", "dotnet --version", "dotnet --list-sdks",
            "npm install", "npm run", "npm test", "npm list", "npm --version", "npm ci", "npm audit",
            "node --version", "yarn --version", "yarn install", "yarn build", "yarn test",
            "cls", "clear", "help", "man",
        };

        private static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "del", "rm", "rmdir", "rd", "erase",
            "move", "mv", "rename", "ren",
            "copy", "cp", "xcopy", "robocopy",
            "mkdir", "md",
            "git push", "git pull", "git merge", "git rebase", "git reset", "git clean", "git checkout", "git commit", "git add", "git rm", "git stash",
            "taskkill", "kill", "shutdown", "restart",
            "npm install -g", "npm uninstall", "dotnet tool install",
        };

        private static readonly HashSet<string> BlockedCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "format", "diskpart", "reg", "regedit", "net user", "net localgroup",
            "powershell -enc", "cmd /c", "rm -rf /", "del /s /q c:\\",
        };

        public static void Initialize(string mode)
        {
            _mode = mode.ToLowerInvariant();
            var startDir = Environment.GetEnvironmentVariable("SHELL_MCP_START_DIR");
            if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                _currentDirectory = startDir;

            // Set SSH Bridge path from environment
            var bridgePath = Environment.GetEnvironmentVariable("SSH_BRIDGE_PATH");
            if (!string.IsNullOrEmpty(bridgePath))
                SshBridgeClient.SetBridgePath(bridgePath);
        }

        public static string Mode => _mode;
        public static string CurrentDirectory => _currentDirectory;

        public static bool IsCommandAllowed(string command, out string reason)
        {
            reason = "";
            string cmdLower = command.ToLowerInvariant().Trim();
            string firstWord = cmdLower.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

            foreach (var blocked in BlockedCommands)
            {
                if (cmdLower.Contains(blocked.ToLowerInvariant()))
                {
                    reason = $"Command '{blocked}' is blocked for safety";
                    return false;
                }
            }

            if (_mode == "safe")
            {
                bool isSafe = SafeCommands.Any(safe => 
                    cmdLower.StartsWith(safe.ToLowerInvariant()) ||
                    firstWord == safe.ToLowerInvariant().Split(' ')[0]);

                if (!isSafe)
                {
                    reason = $"Command '{firstWord}' is not in the safe list. Use shell_dangerous for this operation.";
                    return false;
                }
            }
            else if (_mode == "dangerous")
            {
                bool isAllowed = SafeCommands.Any(safe => 
                    cmdLower.StartsWith(safe.ToLowerInvariant()) ||
                    firstWord == safe.ToLowerInvariant().Split(' ')[0]) ||
                    DangerousCommands.Any(dangerous => 
                    cmdLower.StartsWith(dangerous.ToLowerInvariant()) ||
                    firstWord == dangerous.ToLowerInvariant().Split(' ')[0]);

                if (!isAllowed)
                {
                    reason = $"Command '{firstWord}' is not recognized.";
                    return false;
                }
            }

            return true;
        }

        public static CommandResult Execute(string command, int? timeoutSeconds = null)
        {
            var result = new CommandResult { Command = command };
            int timeout = timeoutSeconds ?? _defaultTimeout;

            try
            {
                if (command.Trim().StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
                    command.Trim().Equals("cd", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleCd(command);
                }

                if (!IsCommandAllowed(command, out string reason))
                {
                    result.Success = false;
                    result.Stderr = reason;
                    result.ExitCode = -1;
                    return result;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    WorkingDirectory = _currentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var process = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                var sw = Stopwatch.StartNew();
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool completed = process.WaitForExit(timeout * 1000);
                sw.Stop();

                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    result.Success = false;
                    result.Stderr = $"Command timed out after {timeout} seconds";
                    result.ExitCode = -1;
                    result.TimedOut = true;
                }
                else
                {
                    result.Success = process.ExitCode == 0;
                    result.ExitCode = process.ExitCode;
                    result.Stdout = stdout.ToString().TrimEnd();
                    result.Stderr = stderr.ToString().TrimEnd();
                }

                result.ExecutionTimeMs = sw.ElapsedMilliseconds;
                result.WorkingDirectory = _currentDirectory;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Stderr = $"Execution error: {ex.Message}";
                result.ExitCode = -1;
            }

            return result;
        }

        private static CommandResult HandleCd(string command)
        {
            var result = new CommandResult { Command = command };
            var parts = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length == 1)
            {
                result.Success = true;
                result.Stdout = _currentDirectory;
                result.WorkingDirectory = _currentDirectory;
                return result;
            }

            string targetPath = parts[1].Trim().Trim('"');
            string newPath;

            if (Path.IsPathRooted(targetPath))
                newPath = targetPath;
            else if (targetPath == "..")
                newPath = Path.GetDirectoryName(_currentDirectory) ?? _currentDirectory;
            else if (targetPath == "~")
                newPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            else
                newPath = Path.Combine(_currentDirectory, targetPath);

            newPath = Path.GetFullPath(newPath);

            if (Directory.Exists(newPath))
            {
                _currentDirectory = newPath;
                result.Success = true;
                result.Stdout = _currentDirectory;
                result.WorkingDirectory = _currentDirectory;
            }
            else
            {
                result.Success = false;
                result.Stderr = $"Directory not found: {newPath}";
                result.ExitCode = 1;
                result.WorkingDirectory = _currentDirectory;
            }

            return result;
        }

        public static string GetAllowedCommands()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Mode: {_mode}");
            sb.AppendLine();
            sb.AppendLine("Safe commands (always allowed):");
            foreach (var cmd in SafeCommands.OrderBy(c => c))
                sb.AppendLine($"  - {cmd}");

            if (_mode == "dangerous")
            {
                sb.AppendLine();
                sb.AppendLine("Dangerous commands (available in this mode):");
                foreach (var cmd in DangerousCommands.OrderBy(c => c))
                    sb.AppendLine($"  - {cmd}");
            }

            sb.AppendLine();
            sb.AppendLine("Blocked commands (never allowed):");
            foreach (var cmd in BlockedCommands.OrderBy(c => c))
                sb.AppendLine($"  - {cmd}");

            return sb.ToString();
        }
    }

    public class CommandResult
    {
        public string Command { get; set; } = "";
        public bool Success { get; set; }
        public string Stdout { get; set; } = "";
        public string Stderr { get; set; } = "";
        public int ExitCode { get; set; }
        public long ExecutionTimeMs { get; set; }
        public string WorkingDirectory { get; set; } = "";
        public bool TimedOut { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(Stdout))
                sb.AppendLine(Stdout);
            if (!string.IsNullOrEmpty(Stderr))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"[stderr] {Stderr}");
            }
            sb.AppendLine();
            sb.AppendLine($"[exit: {ExitCode}] [time: {ExecutionTimeMs}ms] [cwd: {WorkingDirectory}]");
            if (TimedOut)
                sb.AppendLine("[TIMED OUT]");
            return sb.ToString();
        }
    }

    // ===============================================
    // SSH BRIDGE CLIENT
    // ===============================================

    public static class SshBridgeClient
    {
        private const int PORT = 52718;

        private static string? _bridgePath;

        public static void SetBridgePath(string path)
        {
            _bridgePath = path;
        }

        public static string GetStatus()
        {
            try
            {
                return SendCommand("__STATUS__");
            }
            catch
            {
                return "DISCONNECTED";
            }
        }

        public static bool LaunchBridge()
        {
            if (string.IsNullOrEmpty(_bridgePath) || !System.IO.File.Exists(_bridgePath))
                return false;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _bridgePath,
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsBridgeRunning()
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                client.Connect("127.0.0.1", PORT);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string SendCommand(string command)
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect("127.0.0.1", PORT);

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            writer.WriteLine(command);
            var response = reader.ReadLine() ?? "No response";
            // Decode newlines
            return response.Replace("<<CRLF>>", "\r\n").Replace("<<LF>>", "\n").Replace("<<CR>>", "\r");
        }

        public static string Prefill(string host, int port, string user, string? password = null)
        {
            var cmd = $"__PREFILL__:{host}:{port}:{user}";
            if (!string.IsNullOrEmpty(password))
                cmd += $":{password}";
            return SendCommand(cmd);
        }

        public static string TriggerConnect()
        {
            return SendCommand("__CONNECT__");
        }

        public static string GetPenStatus()
        {
            try
            {
                return SendCommand("__PEN_STATUS__");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string PutPenDown()
        {
            try
            {
                return SendCommand("__PEN_DOWN__");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string SendAbort()
        {
            try
            {
                return SendCommand("__ABORT__");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string IsRunning()
        {
            try
            {
                return SendCommand("__IS_RUNNING__");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string KillPort(int port)
        {
            try
            {
                return SendCommand($"__KILL_PORT__:{port}");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string SetTimeout(int seconds)
        {
            try
            {
                return SendCommand($"__TIMEOUT__:{seconds}");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string GetTail()
        {
            try
            {
                return SendCommand("__TAIL__");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string ListSpawned()
        {
            try
            {
                return SendCommand("__LIST_SPAWNED__");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string Spawn(string name, string command)
        {
            try
            {
                return SendCommand($"__SPAWN__:{name}:{command}");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string KillSpawned(string name)
        {
            try
            {
                return SendCommand($"__KILL_SPAWNED__:{name}");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string WriteFile(string path, string content)
        {
            try
            {
                // Encode newlines for line-based protocol
                var encoded = content
                    .Replace("\r\n", "<<CRLF>>")
                    .Replace("\n", "<<LF>>")
                    .Replace("\r", "<<CR>>");
                return SendCommand($"__WRITE_FILE__:{path}|{encoded}");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }

        public static string AppendFile(string path, string content)
        {
            try
            {
                var encoded = content
                    .Replace("\r\n", "<<CRLF>>")
                    .Replace("\n", "<<LF>>")
                    .Replace("\r", "<<CR>>");
                return SendCommand($"__APPEND_FILE__:{path}|{encoded}");
            }
            catch
            {
                return "BRIDGE_NOT_RUNNING";
            }
        }
    }

    // ===============================================
    // MCP TOOLS
    // ===============================================

    [McpServerToolType]
    public static class ShellTools
    {
        [McpServerTool, Description("Execute a shell command. Working directory persists across calls. Use 'cd' to navigate. Check 'shell_info' for allowed commands.")]
        public static string Shell(
            [Description("Command to execute")] string command,
            [Description("Timeout in seconds (default: 30)")] int? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(command))
                return "Error: No command provided";

            var result = ShellExecutor.Execute(command, timeout);
            return result.Success ? result.ToString() : $"❌ Command failed:\n{result}";
        }

        [McpServerTool, Description("Get current working directory")]
        public static string Pwd()
        {
            return ShellExecutor.CurrentDirectory;
        }

        [McpServerTool, Description("Show shell mode and list of allowed commands")]
        public static string ShellInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Shell MCP - {ShellExecutor.Mode.ToUpperInvariant()} mode");
            sb.AppendLine($"Current directory: {ShellExecutor.CurrentDirectory}");
            sb.AppendLine();
            sb.AppendLine(ShellExecutor.GetAllowedCommands());
            return sb.ToString();
        }

        [McpServerTool, Description("Execute multiple commands in sequence. Stops on first failure unless continue_on_error is true.")]
        public static string ShellBatch(
            [Description("JSON array of commands: [\"cmd1\", \"cmd2\", ...]")] string commands_json,
            [Description("Continue executing even if a command fails")] bool continue_on_error = false,
            [Description("Timeout per command in seconds")] int? timeout = null)
        {
            try
            {
                var commands = JsonSerializer.Deserialize<List<string>>(commands_json);
                if (commands == null || commands.Count == 0)
                    return "Error: No commands provided";

                var sb = new StringBuilder();
                int succeeded = 0, failed = 0;

                foreach (var cmd in commands)
                {
                    sb.AppendLine($"$ {cmd}");
                    var result = ShellExecutor.Execute(cmd, timeout);
                    sb.AppendLine(result.ToString());

                    if (result.Success)
                        succeeded++;
                    else
                    {
                        failed++;
                        if (!continue_on_error)
                        {
                            sb.AppendLine($"❌ Batch stopped. {succeeded} succeeded, {failed} failed, {commands.Count - succeeded - failed} skipped.");
                            return sb.ToString();
                        }
                    }
                }

                sb.AppendLine($"✅ Batch complete: {succeeded} succeeded, {failed} failed");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error parsing commands: {ex.Message}";
            }
        }

        [McpServerTool, Description("Execute a command on a remote server via SSH Bridge. Requires SSH Bridge app to be running and connected.")]
        public static string SshCommand(
            [Description("Command to execute on the remote server")] string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return "Error: No command provided";

            try
            {
                // Check if bridge is running, launch if not
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    if (SshBridgeClient.LaunchBridge())
                    {
                        return "🚀 SSH Bridge launched!\n\nPlease:\n1. Enter host, user, and password in the window that just opened\n2. Click Connect\n3. Try this command again";
                    }
                    else
                    {
                        return "❌ SSH Bridge not running and could not auto-launch.\n\nPlease run ssh-bridge.exe manually, or set SSH_BRIDGE_PATH in your Claude config.";
                    }
                }

                var status = SshBridgeClient.GetStatus();
                if (status == "DISCONNECTED")
                {
                    return "⚠️ SSH Bridge is open but not connected.\n\nPlease:\n1. Enter host, user, and password\n2. Click Connect\n3. Try this command again";
                }

                var result = SshBridgeClient.SendCommand(command);
                return $"📡 {status}\n\n{result}";
            }
            catch (Exception ex)
            {
                return $"❌ SSH error: {ex.Message}";
            }
        }

        [McpServerTool, Description("Check if SSH Bridge is connected")]
        public static string SshStatus()
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var status = SshBridgeClient.GetStatus();
                if (status.StartsWith("CONNECTED:"))
                {
                    return $"✅ {status.Replace("CONNECTED:", "Connected to ")}";
                }
                return "⚠️ SSH Bridge open but not connected";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Pre-fill SSH Bridge connection details. Launches SSH Bridge if not running, fills in host/port/user, optionally password. User must click Connect or you can call with auto_connect=true.")]
        public static string SshPrefill(
            [Description("SSH host/IP address")] string host,
            [Description("SSH username")] string user,
            [Description("SSH port (default: 22)")] int port = 22,
            [Description("SSH password (optional - user can enter manually for security)")] string? password = null,
            [Description("Automatically click Connect after prefilling")] bool auto_connect = false)
        {
            try
            {
                // Launch bridge if not running
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    if (!SshBridgeClient.LaunchBridge())
                    {
                        return "❌ Could not launch SSH Bridge. Set SSH_BRIDGE_PATH in your Claude config.";
                    }
                    // Wait for it to start
                    Thread.Sleep(1000);
                }

                // Prefill the fields
                var result = SshBridgeClient.Prefill(host, port, user, password);
                if (result != "PREFILLED")
                {
                    return $"❌ Prefill failed: {result}";
                }

                if (auto_connect && !string.IsNullOrEmpty(password))
                {
                    Thread.Sleep(200);
                    SshBridgeClient.TriggerConnect();
                    Thread.Sleep(2000); // Wait for connection
                    var status = SshBridgeClient.GetStatus();
                    if (status.StartsWith("CONNECTED:"))
                    {
                        return $"✅ Connected to {user}@{host}:{port}";
                    }
                    return $"⚠️ Connection initiated. Status: {status}";
                }

                if (string.IsNullOrEmpty(password))
                {
                    return $"📝 SSH Bridge prefilled with {user}@{host}:{port}\n\nPlease enter password and click Connect.";
                }
                return $"📝 SSH Bridge prefilled with {user}@{host}:{port}\n\nClick Connect when ready.";
            }
            catch (Exception ex)
            {
                return $"❌ Error: {ex.Message}";
            }
        }

        [McpServerTool, Description("Check if user has lifted the pen (paused command execution). When pen is lifted, commands will be blocked until pen is put back down.")]
        public static string SshPenStatus()
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var status = SshBridgeClient.GetPenStatus();
                return status switch
                {
                    "PEN_LIFTED" => "✋ Pen is LIFTED - User has paused command execution. Wait for user or call SshPenDown to request resumption.",
                    "PEN_DOWN" => "✏️ Pen is DOWN - Commands will execute normally.",
                    _ => $"⚠️ Unknown status: {status}"
                };
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Request to put the pen back down (resume command execution). Only works if pen was lifted. User can also click the button manually.")]
        public static string SshPenDown()
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.PutPenDown();
                return result switch
                {
                    "PEN_LOWERED" => "✏️ Pen lowered - Command execution resumed.",
                    "PEN_ALREADY_DOWN" => "✏️ Pen was already down - Commands executing normally.",
                    _ => $"⚠️ Unexpected response: {result}"
                };
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Send Ctrl+C (abort signal) to the currently running command. Use this when a command is taking too long or appears stuck.")]
        public static string SshAbort()
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.SendAbort();
                return result switch
                {
                    "ABORT_SENT" => "🛑 Abort signal (Ctrl+C) sent to running command.",
                    "NO_COMMAND_RUNNING" => "ℹ️ No command currently running.",
                    _ => $"⚠️ Response: {result}"
                };
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Check if a command is currently running on the SSH connection.")]
        public static string SshIsRunning()
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.IsRunning();
                return result switch
                {
                    "RUNNING" => "⚡ A command is currently running.",
                    "IDLE" => "✅ No command running - ready for new commands.",
                    _ => $"⚠️ Status: {result}"
                };
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Kill process listening on a specific port. Useful for freeing up ports or killing hung services.")]
        public static string SshKillPort(
            [Description("Port number to kill (1-65535)")] int port)
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                if (port < 1 || port > 65535)
                {
                    return "❌ Invalid port number. Must be 1-65535.";
                }

                var result = SshBridgeClient.KillPort(port);
                return $"🔫 Kill port {port}:\n{result}";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Set timeout for the next SSH command. Useful for long-running operations like large downloads.")]
        public static string SshSetTimeout(
            [Description("Timeout in seconds (1-3600)")] int seconds)
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                if (seconds < 1 || seconds > 3600)
                {
                    return "❌ Invalid timeout. Must be 1-3600 seconds.";
                }

                var result = SshBridgeClient.SetTimeout(seconds);
                return $"⏱️ {result}";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Get the last 50 lines of output from the SSH Bridge terminal. Useful for checking progress of background tasks.")]
        public static string SshTail()
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.GetTail();
                return $"📜 Recent output:\n{result}";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("List all tracked background processes spawned via SshSpawn.")]
        public static string SshListSpawned()
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.ListSpawned();
                return $"📋 Background processes:\n{result}";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Spawn a background process with a trackable name. Use SshListSpawned to see status and SshKillSpawned to terminate.")]
        public static string SshSpawn(
            [Description("Name to identify this background process")] string name,
            [Description("Command to run in background")] string command)
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.Spawn(name, command);
                return $"🚀 {result}";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Kill a background process by name (spawned via SshSpawn or matching window title).")]
        public static string SshKillSpawned(
            [Description("Name of the background process to kill")] string name)
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.KillSpawned(name);
                return $"🔫 {result}";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Write content to a file on the remote system without shell escaping. Perfect for batch files, configs, etc.")]
        public static string SshWriteFile(
            [Description("Full path to file (e.g., C:\\scripts\\run.bat)")] string path,
            [Description("Content to write - characters like & | < > will NOT be escaped")] string content)
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.WriteFile(path, content);
                return $"📝 {result}";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }

        [McpServerTool, Description("Append content to a file on the remote system without shell escaping.")]
        public static string SshAppendFile(
            [Description("Full path to file")] string path,
            [Description("Content to append - characters like & | < > will NOT be escaped")] string content)
        {
            try
            {
                if (!SshBridgeClient.IsBridgeRunning())
                {
                    return "❌ SSH Bridge not running";
                }

                var result = SshBridgeClient.AppendFile(path, content);
                return $"📝 {result}";
            }
            catch
            {
                return "❌ SSH Bridge not running";
            }
        }
    }
}
