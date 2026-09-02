# Run CabinetNC Cut (local)

Path: `C:\Users\yino\Projects\cabinetnc-cut`

## Vite prototype (browser)
```powershell
cd C:\Users\yino\Projects\cabinetnc-cut
npm install
npm run dev
```
Open http://localhost:5177/

## .NET Desktop (WPF)
Requires .NET 10 SDK (already installed).
Desktop shortcut **CabinetNC Cut** opens `dist\CabinetNC-Cut\` (the only runnable copy).
```powershell
cd E:\Work\OmniCam\dotnet
dotnet build src\CabinetNC.Desktop -c Release
```
Then start from the desktop icon, or `dist\CabinetNC-Cut\Start-CabinetNC-Cut.cmd`.

## Portable web shell
Unzip `CabinetNC-Cut-v0.1.0-portable.zip` and double-click `start.bat` (needs Node).
