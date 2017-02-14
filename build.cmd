"C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild" amiitool-csharp.sln /t:Build /p:Configuration=Release
pause
xcopy amiitool-csharp\bin\Release\* ..\tools\amiitool\ /Y