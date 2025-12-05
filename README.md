# Shell MCP Server

Terminal access for Claude with two security modes, plus SSH bridge for remote servers.

## Features

- **Local shell** with safe/dangerous command separation
- **SSH Bridge** - GUI app for secure remote server access
- **Persistent sessions** - `cd` and environment persist across commands
- **Full visibility** - see every command Claude runs
- **Lift Pen** - pause Claude mid-task to get attention
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
- **Lift Pen** button pauses Claude's command execution
- **Pin** button keeps window on top
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
   - **📌 Pin** - Keep window on top of other windows
   - **✏️ Lift Pen** - Pause Claude's command execution
   - **Disconnect** - End session and revoke access

### Lift Pen Feature

Click "Lift Pen" when you want Claude to stop and wait:
- Claude's next command will be blocked
- You'll see `[BLOCKED - Pen lifted by user]` in the output
- Claude receives a message explaining the pause
- Click the button again (now "Pen Up!") to resume
- Or Claude can call `SshPenDown` to request resumption

This is useful for:
- Getting Claude's attention during a long task
- Reviewing what Claude is doing before continuing
- Taking a break without losing the session

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
- Lift Pen = instant pause
- Disconnect button = instant revoke
- No password persistence

## Dependencies

- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) - MCP SDK for .NET
- [SSH.NET](https://www.nuget.org/packages/SSH.NET) - SSH client library

## License

MIT
