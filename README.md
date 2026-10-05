# GameCoreParker
Manage CPU cores automatically with hotkeys on specific applications (if the window is focused) without the need of drivers, game mode and game bar. Great for X3D multi-CCD CPUs.

<img width="870" height="665" alt="Screenshot 2026-10-04 222551" src="https://github.com/user-attachments/assets/625453bc-c56e-45bd-a469-14242a3cfe2d" />

# Features:

- Handles core manangement automatically based on the executables currently tagged. Depending on the tag you set, it'll set the affinity or cpuset on next background check.
- Two sets of core management. Affinity Mode and CPUSet.
- Affinity Mode: Sets the cores based on your affinity mask to which cores should be used and which ones it'll lock the process from. This is more of a hard lock.
- CPUSet Mode: Sets the cores based on the affinity mask, but doesn't set the actual affinity. Instead, it priorities which CPU cores are used first. If workload is high, it'll use the other cores as needed. This counts more of a soft-lock method. This tends to work better for games with anti-cheats.

# Installation
Simply drag the executable to a location of your choice and double click it to launch it into the background. 
To confirm it launched, press **CTRL+ALT+T** and check your taskbar for a window that appears.

Now simply set which cores should be set with affinity mode and CPUSet. You can also make it so it starts on Windows boot. Press **ENTER** or **S** to return the window to the background.
_If you wish to more easily exit the app when its hidden in the background, open the settings and then close the app._

If you want to have the application start with the window open for debugging or informational purposes, add the argument _**'-show'**_

To tag an application to use these masks, refer to the hotkeys below. Once an application is tagged, it sets whichever mode you set, and is saved for future use. **The config file will be saved along where you placed the executable.** Next time you launch the application, it'll load those games and your CPU mask.

**Make sure you have set your CPU mask during this set up or else the game will not set any mode.**

# Hotkeys:

**ALT + 9** = Toggle the currently focused window to be added to the list of applications that'll have it set via AffinityMask

**ALT + 0** = Toggle the currently focused window to be added to the list of applications that'll have it set via CPUSet

**CTRL + ALT + T** = Open the settings console window. 
_If you wish to more easily exit the app when its hidden in the background, open the settings and then close the app._
