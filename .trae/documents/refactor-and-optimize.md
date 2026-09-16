# 重构优化计划

## Context
项目中多个文件存在重复的辅助方法和模式，随着功能迭代（文件同步、进程管理、传输命名等），代码冗余逐渐增多。本次重构目标：消除重复代码、提取公共工具方法、统一命名规则，不改变任何功能行为。

## 重构项

### 1. 提取 `FileHelper` 工具类
**问题**：`UniquePath` 和 `SanitizeName` 在 3 处重复定义：
- `PeerClient.cs` L331-348 (`SanitizeFileName` + `UniquePath`)
- `TransferEngine.cs` L216-233 (`SanitizeName` + `UniquePath`)
- `MainWindow.xaml.cs` L169-184 (`SanitizeName`)

**方案**：新建 `Util/FileHelper.cs`，统一为：
```csharp
public static class FileHelper
{
    public static string SanitizeName(string name);
    public static string UniquePath(string path, bool isDir);
}
```
三处调用改为引用 `FileHelper`，删除各自的本地副本。

### 2. 提取 `SaveIpHistory` 方法
**问题**：`_history.Upsert(cboPeerIp.Text.Trim(), port); ReloadHistoryCombo();` 重复 7 次（MainWindow.Operation.cs ×5，MainWindow.xaml.cs ×2）。

**方案**：在 MainWindow 中提取：
```csharp
private void SaveIpHistory(int port)
{
    _history.Upsert(cboPeerIp.Text.Trim(), port);
    ReloadHistoryCombo();
}
```
7 处 `if (okIps.Count > 0) { ... }` 改为 `if (okIps.Count > 0) SaveIpHistory(port);`。

### 3. 合并 `GetDeviceInfo` 和 `ProbeDeviceNameAsync`
**问题**：两者都连接远端探测设备信息，逻辑相似但分散在不同文件：
- `MainWindow.xaml.cs` L728-737 `GetDeviceInfo(string host)` — 返回完整信息字符串
- `MainWindow.Operation.cs` L393-405 `ProbeDeviceNameAsync(string host, int port)` — 只返回设备名，失败时回退为 host

**方案**：统一为 `GetRemoteDeviceNameAsync(string host, int port)` 返回设备名（失败回退 host），`GetDeviceInfo` 调用它并拼接显示文本。减少重复连接逻辑。

### 4. 提取 `ExcludeLocalHosts` 到工具类
**问题**：`ExcludeLocalHosts` 定义在 MainWindow.Operation.cs 中，依赖 MainWindow 的 `GetLocalIPs()` 和 `LogLine`。

**方案**：将 IP 过滤逻辑提取到 `Util/HostHelper.cs`：
```csharp
public static class HostHelper
{
    public static List<string> GetLocalIPs();
    public static bool IsLocalIP(string ip);
    public static (List<string> remote, List<string> skipped) FilterLocalHosts(List<string> hosts);
}
```
MainWindow 的 `GetLocalIPs` 和 `ExcludeLocalHosts` 改为委托调用。

### 5. 移除 `IpHelper.NormalizeInput`
**问题**：`NormalizeInput` 只是 `text.Trim()` 的包装，无实际用处。

**方案**：删除该方法，直接用 `.Trim()`。

## 文件变更清单
| 文件 | 操作 |
|------|------|
| `Util/FileHelper.cs` | 新建 |
| `Util/HostHelper.cs` | 新建 |
| `Net/PeerClient.cs` | 删除 SanitizeFileName/UniquePath，改用 FileHelper |
| `Transfer/TransferEngine.cs` | 删除 SanitizeName/UniquePath，改用 FileHelper |
| `MainWindow.xaml.cs` | 删除 GetLocalIPs/SanitizeName，移到 HostHelper；GetDeviceInfo 简化 |
| `MainWindow.Operation.cs` | ExcludeLocalHosts 改用 HostHelper；提取 SaveIpHistory；简化 ProbeDeviceNameAsync |
| `Util/IpHelper.cs` | 删除 NormalizeInput |

## 验证
1. `dotnet build` 编译 0 错误
2. 功能验证：启动服务、文件同步推送/拉取、BBK 更新、进程管理开关、历史记录保存——行为不变
