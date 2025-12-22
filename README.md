---
license: mit
tags:
  - mcp
  - claude
  - shell
  - ssh
  - windows
  - dotnet
language:
  - en
---

# Shell MCP Server

Terminal access for Claude with two security modes, plus SSH bridge for remote servers.

## Features

- **Local shell** with safe/dangerous command separation
- **SSH Bridge** - GUI app for secure remote server access
- **Lift Pen** - Pause Claude's command execution instantly
- **Sudo support** - Auto-send password for sudo commands (opt-in)
- **Full visibility** - See every command Claude runs in real-time
- **Background processes** - Spawn and manage long-running tasks
- **File operations** - Write files without shell escaping issues

## Quick Start (Pre-built Binaries)

Download from the `release/` folder:
- `shell-mcp.dll` - MCP server for Claude Desktop
- `ssh-bridge.exe` - GUI for SSH connections

No build required - just configure Claude Desktop (see below).

## Components

### 1. Shell MCP (`shell-mcp.dll`)
Local Windows terminal access with configurable command allowlists.

### 2. SSH Bridge (`ssh-bridge.exe`)
WinForms GUI that:
- You authenticate with password (held in memory only)
- Claude sends commands through it
- Real-time output display with syntax highlighting
- **Lift Pen** button to pause Claude instantly
- **Sudo** button to enable auto-password for sudo commands
- **Pin** button to keep window on top
- Right-click context menu: Copy, Copy All, Clear

## Installation

### Option 1: Use Pre-built Binaries
1. Download files from `release/` folder
2. Configure Claude Desktop (see below)

### Option 2: Build from Source

**Prerequisites:**
- .NET 8.0 SDK
- Windows 10/11

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
      "args": ["C:\\path\\to\\release\\shell-mcp.dll"],
      "env": {
        "SHELL_MCP_MODE": "safe",
        "SHELL_MCP_START_DIR": "C:\\your\\workspace",
        "SSH_BRIDGE_PATH": "C:\\path\\to\\release\\ssh-bridge.exe"
      }
    },
    "shell_dangerous": {
      "command": "dotnet",
      "args": ["C:\\path\\to\\release\\shell-mcp.dll"],
      "env": {
        "SHELL_MCP_MODE": "dangerous",
        "SHELL_MCP_START_DIR": "C:\\your\\workspace",
        "SSH_BRIDGE_PATH": "C:\\path\\to\\release\\ssh-bridge.exe"
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
| `Shell` | Execute a local command with optional timeout |
| `Pwd` | Get current working directory |
| `ShellInfo` | Show mode and list of allowed commands |
| `ShellBatch` | Run multiple commands in sequence |

### SSH Tools
| Tool | Description |
|------|-------------|
| `SshCommand` | Execute command on remote server |
| `SshStatus` | Check if SSH Bridge is connected |
| `SshPrefill` | Pre-fill connection details and optionally auto-connect |
| `SshPenStatus` | Check if user has paused execution (pen lifted) |
| `SshPenDown` | Request to resume execution |
| `SshAbort` | Send Ctrl+C to abort running command |
| `SshIsRunning` | Check if a command is currently executing |
| `SshSetTimeout` | Set timeout for next command (1-3600 seconds) |
| `SshTail` | Get last 50 lines of terminal output |
| `SshKillPort` | Kill process listening on a specific port |
| `SshSpawn` | Start a background process with a trackable name |
| `SshListSpawned` | List all tracked background processes |
| `SshKillSpawned` | Kill a background process by name |
| `SshWriteFile` | Write content to file without shell escaping |
| `SshAppendFile` | Append content to file without shell escaping |

## SSH Bridge Features

### Lift Pen (Pause Claude)
Click **Lift Pen** to immediately pause Claude's command execution. Any running command is aborted, and new commands are blocked until you click again to resume. Perfect for:
- Reviewing what Claude is doing
- Taking manual control temporarily
- Emergency stop

### Sudo Support
Click **Sudo** to enable auto-password entry for sudo commands. When enabled:
- Claude can run `sudo` commands without prompting
- Your password is sent automatically when sudo asks
- Password is only held in memory while connected

### Pin Window
Click **Pin** to keep the SSH Bridge window always on top.

### Interactive Command Blocking
The bridge automatically blocks interactive commands that would break the shell:
- **Editors:** vim, nano, emacs (use `echo` or `SshWriteFile` instead)
- **Pagers:** less, more (use `cat`, `head`, `tail` instead)
- **TUI apps:** htop, top (use `top -b -n 1` or `ps aux`)
- **Databases:** mysql, psql (use `-e` or `-c` flags for queries)
- **Multiplexers:** tmux, screen (not supported)

Each blocked command shows helpful alternatives.

## Security Model

### shell_safe (approve once)
Read-only and build commands:
- `dir`, `ls`, `type`, `cat`, `head`, `tail`, `find`, `grep`, `pwd`, `cd`, `tree`
- `echo`, `date`, `time`, `whoami`, `hostname`
- `git status`, `git log`, `git diff`, `git branch`, `git remote`, `git fetch`, `git show`
- `dotnet build`, `dotnet run`, `dotnet test`, `dotnet restore`
- `npm install`, `npm run`, `npm test`, `npm list`, `npm ci`
- `node --version`, `yarn install`, `yarn build`, `yarn test`

### shell_dangerous (approve each time)
Modifying commands:
- `del`, `rm`, `rmdir`, `move`, `copy`, `mkdir`
- `git push`, `git pull`, `git merge`, `git rebase`, `git reset`, `git commit`, `git add`
- `taskkill`, `shutdown`
- `npm install -g`, `npm uninstall`

### Always blocked
- `format`, `diskpart`, `reg`, `regedit`
- `net user`, `net localgroup`
- `powershell -enc`, `rm -rf /`, `del /s /q c:\`

### SSH Bridge Security
- Password held in memory only - never written to disk
- Lift Pen for instant pause
- Disconnect for instant revoke
- All commands visible in real-time
- Sudo disabled by default

## Output Handling

- Large outputs are automatically truncated (last 150 lines returned to Claude)
- Maximum 500KB per response
- Real-time streaming display in SSH Bridge window
- ANSI escape codes stripped for clean output

## Dependencies

- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) - MCP SDK for .NET
- [SSH.NET](https://www.nuget.org/packages/SSH.NET) - SSH client library
- [BouncyCastle](https://www.nuget.org/packages/BouncyCastle.Cryptography) - Cryptography (SSH.NET dependency)

## License

MIT
