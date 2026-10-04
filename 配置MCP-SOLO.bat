@echo off
chcp 65001 >nul
rem ============================================================
rem  一键接入 TRAE SOLO（最简，V21）。V20 用户请用 配置MCP-SOLO-v20.bat。
rem  双击即可，做两件事：
rem    1) 把 tia-portal 注册进 TRAE SOLO（写入其 User\mcp.json，自动备份 .bak）；
rem    2) 后台自动预热 headless 博途，第一次调用也能秒连（约 1 秒）。
rem  完成后重启 TRAE SOLO 即可使用。
rem ============================================================
set "EXE=%~dp0runtime\v21\TiaMcpServer.exe"
if not exist "%EXE%" set "EXE=%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe"
if not exist "%EXE%" (
    echo [错误] 找不到引擎 exe（runtime\v21 与 tools\...\bin\Release\net48 均不存在）。
    echo 请确认本脚本在交付包/仓库根目录。
    pause
    exit /b 1
)

echo [1/2] 正在把 TIA Portal MCP 注册进 TRAE SOLO ...
"%EXE%" config --host trae-solo %*
if errorlevel 1 (
    echo [失败] 注册未成功，请按上面的提示处理。
    pause
    exit /b 1
)

echo.
echo [2/2] 正在后台预热 headless 博途（首次调用更快；窗口可最小化，勿关）...
rem 用独立窗口常驻预热；用户可随时在该窗口 Ctrl+C，或运行 tia prewarm --stop 关闭。
start "TIA prewarm" /min "%EXE%" prewarm --tia-major-version 21

echo.
echo 全部完成：请重启 TRAE SOLO，在 设置 -^> MCP 中确认 tia-portal 已就绪。
pause
