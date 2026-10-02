# SSVEP Studio Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development for independent content work and review; integration proceeds in this session. Steps use checkboxes for tracking.

**Goal:** Implement the approved first SSVEP desktop application with six editable targets, impedance preparation, real SDK acquisition, single-result selection border, and persistent experiment records.

**Architecture:** Add Ssvep.Studio.App as a separate WPF entry point referencing the existing core. Add focused content, session, impedance and recording classes under Core/Studio; retain the existing speller as a regression reference. One SDK process per connection, serial background recognition, WPF rendering on the UI thread.

**Tech Stack:** C# / .NET 8 / WPF; existing FBCCA, Python bridge and xUnit. Build using .tools/dotnet/dotnet.exe.

**Spec:** D:/PersonalWorkSpace/ssvep/docs/SSVEP实验设计软件架构.md

## Global Constraints

- First software only; no car or robot implementation in this plan.
- Fixed six targets T1–T6, editable text, shape and image content.
- No user controls or package overrides for stimulus encoding, phase, count, sampling rate or window duration.
- One valid result changes the selected target; no three-result confirmation, standalone deduplication or text/event output API.
- Pause/stop/fault clears selection and invalidates in-flight results; resume needs a fresh window.
- Impedance includes unknown and stale states; EEG freshness does not refresh cached impedance.
- Real measurements and hardware performance remain subject to device testing; simulation is visibly distinct.
- Raw EEG, impedance, stimulus frames, recognition and state changes are recorded locally; write failures cannot be silent.

## Task 1: Content packages

Files: new Core/Studio/StudioContent.cs, StudioContentService.cs; tests/StudioContentTests.cs.

Interfaces: namespace Ssvep.Speller.Core.Studio. StudioContentKind { Text, Shape, Image }, StudioShape { Rectangle, Circle, Diamond, Arrow }; StudioTargetContent(string Id, string Name, StudioContentKind Kind, string Text, StudioShape Shape, string? ImageBase64); StudioContentPackage(int Version, string Name, IReadOnlyList<StudioTargetContent> Targets). StudioContentService static CreateDefault(), Validate(StudioContentPackage), Load(string), Save(string, StudioContentPackage), ReadImage(string) returning base64. Validation throws ArgumentException or InvalidDataException. Save/load portable JSON with embedded PNG/JPEG images, unknown JSON fields rejected including nested targets.

- [x] Test save/load of six targets and embedded image after deleting original asset; malformed IDs, empty required content, unknown/forbidden JSON fields and oversized images rejected; existing destination preserved on validation failure.
- [x] Implement strict content-only schema, atomic save, bounded image imports and defaults with text and shapes.
- [x] Run focused tests; independent review before completion.

## Task 2: Session, windows and impedance

Files: new Core/Studio/StudioRuntimeProfile.cs, StudioSessionController.cs, StudioImpedanceMonitor.cs; append impedance receive timestamp to bridge diagnostics; focused tests.

- [x] Test immediate selection, repeated selection, invalid results, stale epoch, pause/resume and stop; rejection of pre-stimulus and bad windows; 625 versus 1250 samples at 250/500 Hz.
- [x] Implement serial recognition lifecycle and snapshot-based session state; fixed internal profile; full-window, finite-sample and continuity checks.
- [x] Test missing/stale/zero/high impedance and independent receive timestamps, then implement monitor and bridge timestamp propagation.

Core interfaces will be authored with the WPF integration in this session and documented by tests. Session owns state and SelectedTargetId; impedance updates and readiness produce immutable snapshots. No new result service.

## Task 3: Desktop application and persistent records

Files: new src/Ssvep.Studio.App project (App, MainWindow, StudioStimulusView, StudioRuntime, StudioRecorder); add to solution; reuse bridge as linked output asset.

- [x] Create independent editor, preparation, experiment and records views. Editor supports all six slots and portable save/load, no timing controls. Preparation shows SDK connection and per-channel impedance; explicit simulation selection.
- [x] Render fixed six targets using existing luminance calculation; outer selection border separated from flickering contents. Start at first rendered frame; pause and minimize/focus loss stop stimulation.
- [x] Wire actual SDK stream, resettable windows, background FBCCA, watchdogs, session generations and fault reporting. Simulation exercises the actual classifier with synthetic samples and is labeled throughout.
- [x] Persist raw samples, frame markers, impedance snapshots, classifications and state changes via bounded background writer; session manifest remains incomplete on failure. View stored session summary and export folder locally.
- [x] Run WPF smoke checks against instantiated application controls, rendering screenshots and synthetic execution, including pause and stale-result behavior.

## Task 4: Verification and delivery

- [x] Full regression tests and Release build; inspect desktop screenshots and rectify issues.
- [x] Independent whole-change review, resolve findings and run affected checks.
- [x] Publish runnable Windows folder with launcher and usage guide; clearly separate completed software checks from outstanding hardware validation.

## Execution notes

The workspace is not a Git repository. Work proceeds in dedicated new application/core directories with a source snapshot for rollback; no repository is initialized and no Git commit/worktree claims will be made. Existing code changes are limited to compatible bridge diagnostics and solution registration. User's instruction to execute approves the existing architecture and implementation; no additional approval gate is needed.
