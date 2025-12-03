# Shell MCP Server

Terminal access for Claude with two security modes, plus SSH bridge for remote servers.

## Features

- **Local shell** with safe/dangerous command separation
- **SSH Bridge** - GUI app for secure remote server access
- **Full visibility** - see every command Claude runs
- **Instant disconnect** - revoke access anytime

## Components

### 1. Shell MCP (`shell-mcp.dll`)
Local Windows terminal access with configurable command allowlists.

### 2. SSH Bridge (`ssh-bridge.exe`)
WinForms app that:
- You authenticate with password (never stored on disk)
- Claude sends commands through it
- You see all commands and output in real-time
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
dotnet build ShellMcp.csproj
dotnet build SshBridge.csproj
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
        "SHELL_MCP_START_DIR": "C:\\your\\workspace"
      }
    },
    "shell_dangerous": {
      "command": "dotnet",
      "args": ["C:\\path\\to\\shell-mcp.dll"],
      "env": {
        "SHELL_MCP_MODE": "dangerous",
        "SHELL_MCP_START_DIR": "C:\\your\\workspace"
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

## SSH Bridge Usage

1. Run `ssh-bridge.exe`
2. Enter host, username, and password
3. Click **Connect**
4. Window shows all commands Claude runs and their output
5. Click **Disconnect** anytime to revoke access

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
- You see every command in real-time
- Disconnect button = instant revoke
- No password persistence

## Dependencies

- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) - MCP SDK for .NET
- [SSH.NET](https://www.nuget.org/packages/SSH.NET) - SSH client library

## License

MIT
