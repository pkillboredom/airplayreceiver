# AirPlay Receiver

Open source implementation of AirPlay 2 Audio protocol for .NET 8.  

This project is based on the work of [SteeBono's airplayreciever](https://github.com/SteeBono/airplayreceiver/). This project would not exist without their work.

Demo app working on Windows with an iPhone 16 on iOS 18. Build, set dump dir, and run to test.

## Issues, Goals

The original airplayreciever project has the protocol and crypto heavy lifting complete, and works as a demo project, but is not ready to be integrated into other software.

The original project:
- Targets .NET Core 2, and thus is outdated.
- Relies on some deprecated or unmaintained libraries.
- Has some weird and specific build instructions for its native dependencies that do not seem to work at present.
- Is set up as a standalone console app.

The goal of this project is to update and modify airplayreciever to be usable as a library and require no dependencies outside NuGet.

As I do not require any video features for my intended downstream application, I will be commenting out those features (or eventually removing them entirely). If anyone using video features ever has an interest in maintaining them, feel free to send a PR.

## Changes

### Completed
- Replaced SteeBono's plist implementation with plist-cil. Needed due to its use of BinaryFormatter.
  - This is currently using a fork but a PR is open.
- Replaced decoding libraries with ones that are simple downloads from NuGet.
- Updated to .NET 8.
- Updated or replaced some other dependencies.
- Fixed a buffer bug when pausing audio.
- Removed video related feature flags since I will not be making use of them.

### To Do
- Turn this from an application into a library.
  - Create a new demo app.
  - Expose volume changes as an event.
- Clean up code.
- Appear to clients as a speaker instead of an Apple TV.