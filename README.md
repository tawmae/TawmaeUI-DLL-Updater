# TawmaeUI DLL Updater

Updates **TawmaeUI.dll** in your Streamer.bot to the latest version from [tawmae.xyz](https://www.tawmae.xyz).

![TawmaeUI DLL Updater](docs/screenshot.png)

## What it's for

Since automatically updating the TawmaeUI.dll isn't really possible due to the nature of how Streamer.bot works, I figured this is the second best alternative. This updater simply checks your TawmaeUI.dll version and if there's a newer one available, it'll automatically replace the old one.

## How to use

1. Download `TawmaeUI-DLL-Updater.zip` from the [latest release](https://github.com/tawmae/TawmaeUI-DLL-Updater/releases/latest).
2. Put `TawmaeUI DLL Updater.exe` from the zip into your Streamer.bot folder, next to where `Streamer.bot.exe` is.
3. Run it. If there is an update, close Streamer.bot and click **Update**.

Note: The exe isn't code-signed, so Windows may show "Windows protected your PC" the first time.
Click **More info**, then **Run anyway**.

## Transparency

I know, .exe files are scary. That's why all of the code is open source, and you don't have to trust
my exe at all: you can build your own from this code.

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (version 8 or newer).
2. Download this code: click the green **Code** button at the top of this page, then **Download ZIP**,
   and extract it.
3. Open a terminal in the extracted folder and run: `dotnet build -c Release`
4. Your exe is in `bin\Release\net48\`. It runs on .NET Framework 4.8, which comes with Windows 10 and 11. YIPPIE!


## License

MIT, see [LICENSE](LICENSE)
