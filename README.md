# About 
Stupid simple Inter-process Service using NamedPipeServer in C#, Dotnet 8
> Made this in 1 morning to learn more about communication between processes, shared memory, and mutex blah blah.

## Problem
There is an application, that we allow the user to run multiple instances of. The app can connect to an instance of a web service based on the user input. When the user runs a tool that take reeeeeaaaaaalllllyyyyy long in the app, they can open a new process to do other stuffs. However, we don't want them to change the instance of the web service just in case they mixed up which was which and cause a **destructive action** on prod (_shockers_) => We lock that guy up til the tool finish

## Solution
The High-level implementation:
- On launch checks if first instance:
	- YES: Host a communication server
	- NO: Try connect to the host
- The server is responsible for notifying that a process is running => CanvasInstance is immutable until done
- Once process finish announce it too.
- If original host die => Next guy that gets an IOException (NamedPipeServer throws it) will try to be the new host
  - Since this is a new host other processes need to reconnect => check if there's already a new host.
    - YES: Connect BAS
    - NO: Become the host

## Notes
- I simply want to make this a repo since it's a good reference material for Shared memory task in the future, since all the concepts involved can be utilized across fields and languages (Linux, UNIX, and OS too! Wowwww!) (<- I really like those, and wants to learn more)
- It was made with AI to an extent, I know I know, shoutout to the clankers though
- Overall fun little experience
