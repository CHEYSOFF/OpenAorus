# OpenAorus — Registering the WMI schema ourselves — Design

Date: 2026-09-08
Status: approved

## 1. What happened, and why this exists

The owner uninstalled Gigabyte Control Center. Every OpenAorus fan and battery operation
began failing at its first step with "not found", and the machine later froze, plausibly
from heat.

The cause is not a missing method. It is a missing **class**. All three of
`GB_WMIACPI_Get`, `GB_WMIACPI_Set` and `GB_WMIACPI_Event` disappeared from `root\WMI`.

The firmware interface itself is untouched. `ACPI\PNP0C14\DCK` is present and healthy on
the machine right now. What vanished is the **schema**: the declaration that names the
firmware's data blocks and describes their methods. Gigabyte ships that as `acpimof.dll`,
and uninstalling Control Center took it away.

So OpenAorus never depended on Gigabyte's software to reach the hardware. It depended on a
registration file that Gigabyte's installer happened to be the only thing providing. The
README has always listed removing that dependency as future work. It stopped being future
work the moment an uninstall left the machine with a fan mode nothing could change.

## 2. Goal

Register the schema ourselves, so OpenAorus works on a machine with no Gigabyte software
installed at all.

In scope: authoring the MOF from the schema recovered from this machine, registering and
removing it under elevation, and gating every write behind proof that the registration is
correct.

Out of scope: shipping a driver package, replacing the ACPI-WMI mapper, or touching the
firmware.

## 3. What a MOF actually has to say

A WMI class bound to an ACPI data block needs, per class: the `guid` qualifier naming the
firmware block, `dynamic`, `provider("WmiProv")`, and `WMI`. Per method: a `WmiMethodId`,
and each parameter's type, direction and `WmiDataId`.

All of it was recovered from this machine and lives in
`docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt`: four class GUIDs, 143 methods with
their numeric ids, and every parameter signature.

## 4. The risk, stated plainly

A MOF maps names to **numbers**. If a `WmiMethodId` is wrong, the app calls a different
firmware method than the one it believes it is calling, with an argument meant for
something else, on the controller that governs cooling and charging. That is worse than not
working, and it is the failure this design exists to prevent.

Three properties follow from that, and they are not negotiable:

- **Reproduce, do not invent.** The MOF is generated from the recovered dump by a checked-in
  script, and the generated file is checked in beside it. A method nobody recovered does not
  appear. A parameter whose direction was not recorded does not get a guess.
- **Never register over a working schema.** If the classes already exist - Control Center is
  installed, or a previous registration is live - do nothing and say so. Two schemas over
  one GUID is a state nobody has tested.
- **Earn the right to write.** See section 6.

## 5. Registration

`mofcomp.exe` ships with Windows and compiles a MOF into the repository. It needs elevation,
which the app already has.

Registration is an explicit action the owner takes from Settings, never something that
happens silently at startup. It states what it will do before doing it, and it is
removable: a companion MOF using `#pragma deleteclass` takes the classes back out, exposed
as a Remove button beside the install one.

If `mofcomp` reports failure, nothing is half-registered - the classes are absent and the
app reports exactly that.

## 6. Earning the right to write

Registering the schema does not unlock fan control. Two gates do, in order.

**Gate A, reads.** Call every recovered `Get` method. The registration is judged sound only
if the results are internally consistent and match what this machine has already produced:
the model string, a duty scale of 229, two fans, a CPU temperature in a plausible range, and
a fifteen-slot fan table that is monotonic in temperature. A dump from before the uninstall
is checked in for exactly this comparison. Reads cannot damage anything, so this gate is free
to fail loudly.

**Gate A does not prove the Set class.** `Get` and `Set` are separate classes with separate
id spaces, so a correct `Get` mapping says nothing about `Set`. This is the trap.

**Gate B, one harmless round trip.** Read `GetChargeStop`. Call `SetChargeStop` with the
value it just returned. Read it back. A value that survives unchanged proves the `Set` class
resolves to the firmware and that at least one id maps as recorded, using a method that
cannot overheat anything and whose write is a no-op by construction.

Only after both gates pass does the app enable fan and battery writes. Until then it behaves
as it does on an unrecognised model: reads and lighting work, writes are refused with a
reason.

The gate result is recorded, so this is a one-off, not a check on every launch. Registration
changing underneath it - Control Center installed later, someone running `mofcomp` by hand -
invalidates the record.

## 7. What the owner sees

A machine with no schema currently reports "step 1/5 setCurrentFanStep failed: not found",
one step into a sequence, which describes the symptom and not the cause. Instead:

- Startup detects the missing classes and says so once: the interface is not registered,
  what that means, and the button that fixes it.
- Fan and battery controls are disabled rather than offered and then failing.
- The Settings entry explains that this replaces a file Control Center used to install, that
  it needs administrator rights, and that it can be undone.

## 8. What this does not fix

The controller keeps the last mode it was given, and it keeps it whether or not any software
is running. That is deliberate - it is how a custom curve survives closing the app - but it
means a machine can be left on a fixed duty by software that later loses the ability to
change it. Registering our own schema makes that far less likely, since the schema now leaves
with the app rather than with someone else's installer. It does not make it impossible.

Worth considering separately: whether a fixed duty should be written at all without a
watchdog present to release it, and whether the app should restore automatic fan control on
a clean exit. Neither is decided here.

## 9. Verification

Nothing about this can be confirmed by a unit test. The MOF generator can be tested against
the recovered dump, and the gate logic can be tested over fakes, but whether the registration
actually binds to the firmware is a hardware question. `VERIFY.md` gains a section covering:
registration on a machine with no Gigabyte software, both gates passing, fan control working
afterwards, removal putting the machine back, and the behaviour when Control Center is
installed alongside.
