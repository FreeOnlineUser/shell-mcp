# Shell MCP Server

Terminal access for Claude with two security modes, plus SSH bridge for remote servers.

## Features

- **Local shell** with safe/dangerous command separation
- **SSH Bridge** - GUI app for secure remote server access
- **Persistent sessions** - `cd` and environment persist across commands
- **Full visibility** - see every command Claude runs
- **Sudo support** - toggle to allow root access on Linux
- **Lift Pen** - pause Claude mid-task or abort running commands
- **Interactive command blocking** - prevents shell-breaking TUI apps
- **Stay on top** - pin the SSH Bridge window
- **Instant disconnect** - revoke access anytime

## Components

### 1. Shell MCP (`shell-mcp.dll`)
Local Windows terminal access with configurable command allowlists.

### 2. SSH Bridge (`ssh-bridge.exe`)
WinForms app that:
- You authenticate with password (never stored on disk)
- Claude sends commands through it
- You see all commands and output in real-time
- **🔓 Sudo** button enables root access (password auto-sent)
- **✏️ Lift Pen** button pauses Claude or aborts running commands
- **📌 Pin** button keeps window on top
- Right-click output for Copy/Copy All/Clear
- Click Disconnect to revoke access instantly

## Installation

### Prerequisites
- .NET 8.0 SDK
- Windows 10/11

### Build

```bash
git clone https://github.com/FreeOnlineUser/shell-mcp.git
cd shell-mcp
dotnet restore
dotnet build ShellMcp.csproj -c Release
dotnet build SshBridge.csproj -c Release
```

### Configure Claude Desktop

Edit `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "shell_safe": {
      "command": "dotnet",
      "args": ["C:\\path\\to\\shell-mcp.dll"],
      "env": {
        "SHELL_MCP_MODE": "safe",
        "SHELL_MCP_START_DIR": "C:\\your\\workspace",
        "SSH_BRIDGE_PATH": "C:\\path\\to\\ssh-bridge.exe"
      }
    },
    "shell_dangerous": {
      "command": "dotnet",
      "args": ["C:\\path\\to\\shell-mcp.dll"],
      "env": {
        "SHELL_MCP_MODE": "dangerous",
        "SHELL_MCP_START_DIR": "C:\\your\\workspace",
        "SSH_BRIDGE_PATH": "C:\\path\\to\\ssh-bridge.exe"
      }
    }
  }
}
```

**Approval settings:**
- `shell_safe` → "Allow always"
- `shell_dangerous` → "Allow once" (asks every time)

## Tools

### Local Shell
| Tool | Description |
|------|-------------|
| `Shell` | Execute a local command |
| `Pwd` | Get current working directory |
| `ShellInfo` | Show mode and allowed commands |
| `ShellBatch` | Run multiple commands in sequence |

### SSH (requires SSH Bridge running)
| Tool | Description |
|------|-------------|
| `SshCommand` | Execute command on remote server |
| `SshStatus` | Check if SSH Bridge is connected |
| `SshPrefill` | Pre-fill connection details, optionally auto-connect |
| `SshPenStatus` | Check if user has paused execution |
| `SshPenDown` | Request to resume execution |

## SSH Bridge Usage

1. Run `ssh-bridge.exe` (or let Claude auto-launch it)
2. Enter host, username, and password
3. Click **Connect**
4. Window shows all commands Claude runs and their output
5. Use toolbar buttons:
   - **🔓 Sudo** - Enable root access (becomes 🔐 when active)
   - **📌 Pin** - Keep window on top of other windows
   - **✏️ Lift Pen** - Pause Claude's command execution
   - **Disconnect** - End session and revoke access

### Sudo Feature

The Sudo button controls whether Claude can run `sudo` commands:

**Sudo OFF (default):**
- `sudo` commands are blocked before they reach the server
- Claude sees: `SUDO_BLOCKED: Sudo commands are disabled`
- Shell stays clean, no password prompts

**Sudo ON (purple button):**
- `sudo` commands are sent to the server
- Password is automatically sent when prompted
- Claude gets the command output with root privileges

**Security:**
- Password is stored in memory only (cleared on disconnect)
- Password is never sent back to Claude
- You control when sudo is available
- Toggle on/off anytime during the session

### Lift Pen Feature

Click "Lift Pen" when you want Claude to stop and wait:

**Blocking future commands:**
- Claude's next command will be blocked
- You'll see `[BLOCKED - Pen lifted by user]` in the output
- Claude receives a message explaining the pause

**Aborting running commands:**
- If a command is currently running when you lift the pen:
- SSH Bridge sends Ctrl+C to the server immediately
- The command is aborted and Claude gets partial output
- Claude sees `[ABORTED BY USER - Pen lifted]`

**Resuming:**
- Click the button again (now "Pen Up!") to resume
- Or Claude can call `SshPenDown` to request resumption

This is useful for:
- Getting Claude's attention during a long task
- Stopping a command that's taking too long
- Reviewing what Claude is doing before continuing
- Taking a break without losing the session

### Interactive Command Blocking

SSH Bridge automatically blocks commands that would break the non-interactive shell:

**Always blocked:**
- Editors: `vim`, `nano`, `emacs`, `vi`, `nvim`, `pico`, `joe`, `mcedit`
- Pagers: `less`, `more`
- TUI monitors: `htop`, `btop`, `atop`, `nmon`, `glances`
- Terminal multiplexers: `tmux`, `screen`, `byobu`
- File managers: `mc`, `ranger`, `nnn`
- Remote shells: `ssh`, `telnet`
- Man pages: `man`, `info`
- Interactive FTP: `ftp`, `sftp`

**Allowed with flags:**
- `top -b -n 1` (batch mode)
- `mysql -e "SELECT..."` (query flag)
- `psql -c "SELECT..."` (command flag)
- `mongosh --eval "..."` (eval flag)
- `bash -c "command"` (one-liner)

**Helpful alternatives provided:**
```
vim → echo "content" > file.txt
      cat << 'EOF' > file.txt
      sed -i 's/old/new/g' file.txt

htop → ps aux, free -h, df -h, top -b -n 1

mysql → mysql -e "SELECT..."
```

Password is held in memory only while connected - never written to disk.

## Security Model

### shell_safe (approve once)
Read-only and build commands:
- `dir`, `ls`, `type`, `cat`, `pwd`, `cd`
- `git status`, `git log`, `git diff`, `git branch`
- `dotnet build`, `dotnet run`, `dotnet test`
- `npm install`, `npm run`, `npm test`

### shell_dangerous (approve each time)
Modifying commands:
- `del`, `rm`, `rmdir`, `move`, `copy`, `mkdir`
- `git push`, `git commit`, `git reset`
- `taskkill`

### Always blocked
- `format`, `diskpart`, `regedit`
- `net user`, `net localgroup`
- `rm -rf /`, `del /s /q c:\`

### SSH Bridge
- You authenticate manually each session
- Uses persistent ShellStream - `cd` works across commands
- You see every command in real-time
- Sudo toggle = controlled root access
- Lift Pen = instant pause or abort
- Interactive commands = blocked with alternatives
- Disconnect button = instant revoke
- No password persistence to disk

## Comparison with Other MCP Terminals

| Feature | SSH Bridge | ssh-mcp | mcp-ssh-manager | @mako10k |
|---------|------------|---------|-----------------|----------|
| Remote SSH | ✅ | ✅ | ✅ | ❌ |
| Interactive PTY | ✅ | ❌ | ❌ | ✅ |
| Sudo handling | ✅ auto | ✅ flags | ✅ | ❌ |
| Pause/Resume | ✅ Pen | ❌ | ❌ | ❌ |
| Abort running | ✅ Ctrl+C | ❌ | ❌ | ❌ |
| GUI | ✅ | ❌ | ❌ | ❌ |
| Command blocking | ✅ | ❌ | ❌ | ❌ |
| File transfer | ❌ | ❌ | ✅ SFTP | ✅ |
| Multi-session | ❌ | ❌ | ✅ | ✅ |

## Dependencies

- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) - MCP SDK for .NET
- [SSH.NET](https://www.nuget.org/packages/SSH.NET) - SSH client library

## License

MIT
