
set binDir=c:\opt\bin


@rem dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true

copy .\bin\Release\net10.0-windows\win-x64\publish\B4Browse.exe %binDir%
 
