# Sensorimotor Synchronisation Study

A Unity-based experimental application for studying sensorimotor synchronisation with a virtual musical ensemble. Participants tap along with recorded musicians under different audiovisual and interaction conditions, while the application records timing and questionnaire data for later analysis.

## Study design

The study uses a 2 × 3 within-participant design:

| Modality | Ensemble behaviour |
| --- | --- |
| Audio only | Non-adaptive, adaptive, or agentic |
| Audiovisual | Non-adaptive, adaptive, or agentic |

- **Non-adaptive:** the accompanying musicians continue at a fixed tempo.
- **Adaptive:** the accompanying musicians synchronise to the participant-driven leader.
- **Agentic:** the leader and accompanying musicians make mutual timing corrections.

The experiment includes practice trials, repeated trials for each condition, and five post-condition ratings: perceived synchrony, ease of coordination, realism, engagement, and sense of agency.

## Requirements

- Unity `6000.5.3f1`
- A macOS or Windows development environment supported by that Unity release
- Git LFS is recommended because the project contains audio and video assets
- Optional Teensy/FSR tap sensor; mouse input can be used without the hardware

The required ARME native libraries are included in `Assets/Plugins` for the currently configured platforms.

## Getting started

1. Clone the repository or open the local repository in GitHub Desktop.
2. In Unity Hub, select **Add → Add project from disk** and choose the repository folder.
3. Install Unity `6000.5.3f1` if Unity Hub prompts you to do so.
4. Allow Unity to import the assets and restore the packages.
5. Open `Assets/Scenes/User Study.unity`.
6. Press **Play** to run the study in the Unity Editor.

The scene is not currently included in Unity's Build Settings. To create a standalone build, open the User Study scene and add it through **File → Build Profiles** before building.

## Running a session

The experiment interface collects participant details, runs practice trials, and then allows the experimenter to select each of the six condition blocks. The participant taps in time with the Violin 1 part using the configured hardware sensor or mouse input.

Study parameters—including repetitions, practice trials, count-in timing, modality, and synchronisation behaviour—can be configured in the Inspector on the user-study components.

## Data output

Session data is saved outside the repository to:

```text
Desktop/User Study Data/<ParticipantID>/
```

Depending on the logger configuration, the output includes:

- `<PID>_taps.csv` — individual taps and tap-to-leader asynchrony
- `<PID>_summary.csv` — trial-level measures and questionnaire ratings
- `<PID>_session.csv` — participant, session, and engine configuration
- `<PID>_musician_onsets.csv` — actual musical onset timing for each virtual musician
- `<PID>_onsets.csv` — optional timing-model onset data

Practice-trial data is not written to these files.

## Project structure

```text
Assets/ARME/UserStudy/           Experiment flow, interface, and data logging
Assets/ARME/TimingModel/         Ensemble timing model integration
Assets/ARME/PlaybackController/  Onset-based, pitch-preserving playback
Assets/ARME/Ensemble/            Study audio, video, and onset assets
Assets/Scenes/                   User-study and demonstration scenes
Assets/Plugins/                  Native ARME libraries
Packages/                        Unity package configuration
ProjectSettings/                 Unity project settings
```

## Repository notes

Unity-generated folders such as `Library`, `Temp`, `Logs`, and `Builds` are intentionally excluded from version control. Do not commit participant data or other personally identifiable information to this repository.

## License

No licence has been specified yet. Until one is added, the project remains under the copyright of its authors and should not be redistributed without permission.
