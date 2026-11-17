@echo off
cd /d "%~dp0installer"
echo [1/3] Installing DriverCertificate.cer into Root and TrustedPublisher...
certutil -addstore -f root "DriverCertificate.cer"
if errorlevel 1 goto :err
certutil -addstore -f TrustedPublisher "DriverCertificate.cer"
if errorlevel 1 goto :err
echo [2/3] Installing virtual-display-driver-0.3.1-x86_64.msi...
msiexec /i "virtual-display-driver-0.3.1-x86_64.msi" /passive /norestart
if errorlevel 1 goto :err
echo [3/3] Checking service and named pipe...
sc query vdd-user-session-service >nul 2>&1
echo Done!
exit /b 0
:err
echo Failed with errorlevel %errorlevel%.
pause
exit /b %errorlevel%
