# OpenAorus v0.4 — Per-app profiles done right — Design (draft)

Date: 2026-09-06
Status: approved; scheduled after v0.3.

## 1. What Gigabyte's version does, and why it is hated

Gigabyte Control Center ships a feature it calls AI mode. Decompiling it shows there is
no model involved: `ControlCenter\AORUS 17G KD\game.xml`, `performance.xml`,
`creator.xml` and `meeting.xml` are hard-coded lists of executable names, each with a
fan mode, a keyboard effect and an audio preset. A `Win32_ProcessStartTrace` watcher
fires whenever a listed process starts and re-applies the profile.

The result is the behaviour the owner complained about: the fan mode changes by itself,
changing it back does not stick because the watcher re-applies on the next trigger, and
turning the feature off sometimes needs a reboot because the watcher lives in a
separate always-running process.

The lesson is not "per-app profiles are bad". It is that a rule engine which overrides
the user without saying so, and cannot be switched off from the place it acts, is
hostile. v0.4 keeps the useful half and removes the hostile half.

## 2. Goal

Opt-in rules that switch settings when chosen applications run, with three properties
Gigabyte's version lacks: the user's manual choice always wins, every automatic change
is visible, and the whole feature stops instantly when switched off.

In scope:

- Rules the owner writes: match an executable, apply a fan mode and optionally a
  lighting preset
- A visible indication when a rule is driving the current state, naming the rule
- Manual override that suspends automation until the matched app exits
- Restore the previous settings when the last matching app exits
- A global off switch that takes effect immediately, with no background process left

Out of scope:

- Shipping a curated list of games. The owner's rules are the owner's.
- Matching on window titles, GPU load, or anything requiring continuous inspection
- Changing power limits or anything v0.1 deliberately left out

## 3. Rules

A rule is: a name, a set of executable names, a fan mode, an optional lighting preset
name, and an enabled flag. Matching is on process executable name only, case-insensitive,
no paths and no wildcards in v0.4 — a narrow, predictable match beats a clever one.

Rules are ordered. The first enabled rule with a running match wins. When no rule
matches, the settings return to the owner's baseline, which is whatever was active
before the first rule fired.

## 4. Architecture

```
src/OpenAorus.Hardware/Automation/AppRule.cs         name, executables, fan mode, preset, enabled
src/OpenAorus.Hardware/Automation/RuleEngine.cs      pure: running processes + rules -> desired state
src/OpenAorus.Hardware/Automation/AutomationState.cs baseline, active rule, suspended flag
src/OpenAorus.App/Automation/ProcessWatcher.cs       start/stop events, WMI or polling
src/OpenAorus.App/Automation/AutomationService.cs    wires the watcher to the controllers
src/OpenAorus.App/ViewModels/RulesViewModel.cs       rule list editing
src/OpenAorus.App/Views/RulesPanel.xaml(.cs)
```

`RuleEngine` is a pure function from a set of running executable names plus the rule
list to a desired state. No timers, no processes, no IO — so every precedence and
restore question is a unit test, and the parts that need Windows stay thin.

**Process watching.** `Win32_ProcessStartTrace` needs a WMI event subscription and
polls internally; the cheaper and more reliable option is a 2-second timer over
`Process.GetProcesses()` names, which costs almost nothing and has no elevation or
event-storm concerns. v0.4 uses the timer and treats it as an implementation detail
behind `ProcessWatcher`.

## 5. Behaviour, and the three properties that matter

- **Manual wins.** Choosing a fan mode by hand while a rule is active sets a suspended
  flag. The engine will not touch the fans again until the matched application exits.
  The suspension is per-rule-activation, so the next game still gets its profile.
- **Visible.** When a rule is driving the state, the mode row shows a small badge
  reading the rule's name, and the tray tooltip appends it. Nothing changes silently.
- **Instant off.** The global switch stops the timer and clears the state in the same
  call. There is no separate process, so there is nothing left to re-apply anything.
- **Restore.** When the last matching application exits, the baseline captured at
  activation is re-applied, and the badge disappears.

## 6. Testing

- `RuleEngine` tests: no match returns the baseline; first enabled match wins; a
  disabled rule is skipped; two matching rules resolve by order; suspension makes the
  engine return the manual state; exit restores the baseline.
- `ProcessWatcher` tests against an injected process-name source, asserting start and
  stop events fire once per transition.
- Owner verification: launch a game listed in a rule and watch the mode change and the
  badge appear; change the mode by hand and confirm it sticks; quit the game and confirm
  the baseline returns; switch automation off mid-game and confirm nothing moves again.

## 7. Risks

- A 2-second poll can miss an application that starts and exits within the interval.
  That is acceptable: the feature exists for long-running applications.
- Rules that match a background helper rather than the game itself will surprise the
  owner. The rules panel lists currently running executables to pick from, so a rule is
  built from something real rather than typed from memory.
