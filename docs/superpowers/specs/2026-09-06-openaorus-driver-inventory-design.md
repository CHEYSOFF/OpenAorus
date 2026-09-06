# OpenAorus — Driver inventory — Design

Date: 2026-09-06
Status: approved, scope deliberately limited

## 1. The complaint

"You can't update drivers from it properly" was one of the original reasons for
replacing Gigabyte Control Center. Its updater is also implicated in the complaint
next to it: versions that break and have to be downloaded again.

## 2. What this does

Read what is installed and show it plainly. For each Gigabyte-supplied driver on the
machine: the device it belongs to, the installed version, its date, and a link to the
official download page for that model.

Nothing is downloaded. Nothing is executed. The user goes to the vendor page and
decides for themselves.

## 3. What this deliberately does not do, and why

It does not fetch or install drivers.

OpenAorus relaunches itself elevated at startup, so anything it downloaded and ran
would run as administrator. To do that safely the app would have to verify what it
received before executing it, and there is nothing to verify against: Gigabyte
publishes no machine-readable manifest or hashes for driver downloads, so the file
would be whatever a scraped URL returned. Whoever controls that page, the CDN or DNS
on an untrusted network would control what gets installed with administrator rights.

Two further reasons specific to this project:

- Model detection is a guess on machines nobody has tested, which is why an unverified
  model shows a banner. Feeding a guessed model into "install the driver for this
  laptop" installs the wrong driver on the wrong hardware.
- Drivers are kernel-mode. A bad display or chipset driver produces a laptop that does
  not display or does not boot, and without a restore point there is no way back.

This is also GPL software other people install, so a wrong mechanism is wrong for
every user rather than for one.

## 4. What a full updater would require

Recorded so the decision can be revisited rather than re-argued:

- Authenticode signature verification with the publisher pinned to Gigabyte, checked
  before anything is executed
- An explicit per-install confirmation naming the device, the version and the publisher
- A system restore point created first
- A visible record of what was installed and when

That is well-understood work. It was not done because inventory-and-link answers the
actual complaint - "what is installed and is it current" - without turning a fan
utility into a privileged installer.

## 5. Sources

Installed drivers come from `Win32_PnPSignedDriver`, filtered to the vendor's devices.
No network access is required to build the inventory; only the link needs the network,
and the user follows it in their own browser.

## 6. Verification

Nothing here writes to the machine, so the checklist item is only that the inventory
matches Device Manager for a handful of devices, and that each link opens the right
model's page.
