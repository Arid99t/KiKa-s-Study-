# Onset-Based Time Stretching Implementation Plan

## Overview
This document outlines the implementation plan for creating a new ARME playback system that uses onset detection and desired timing to dynamically adjust time-stretching ratios, rather than using a fixed time ratio slider.

## Current System Analysis

### Existing Components:
1. **ARMESimplePlaybackController.cs** - Core C# wrapper with native library bindings
2. **ARMEPlaybackControllerExample.cs** - Unity component with slider-based time ratio control  
3. **ARMEPlaybackEnsembleController.cs** - Multi-controller management system

### Key Existing Functionality:
- `SetRatioFromOnsetTime(float playbackOnsetTime, float stretchedOnsetTime)` - The core method we want to utilize
- Real-time audio processing via Unity's OnAudioFilterRead
- AudioClip loading and mono conversion
- Synchronized ensemble control

## New Implementation Requirements

### 1. Onset File Integration
- **Input**: .txt file containing onset times from original audio
- **Parser**: OnValidate function to read and parse onset times into `List<float>`
- **Format**: Simple text file with one float time value per line (in seconds)

### 2. Desired Timing Configuration
- **Configurable interval**: User-specified desired interval between onsets (e.g., 0.3 seconds)
- **Auto-generation**: Automatically generate desired onset times as multiples of the interval
  - Example: 0.0s, 0.3s, 0.6s, 0.9s, 1.2s, etc.
- **Manual override**: Option to load custom desired onset times from file if needed

### 3. Dynamic Time Ratio Adjustment
- **Ensemble Controller Responsibility**: 
  - Monitor global playback time continuously
  - Schedule and trigger `SetRatioFromOnsetTime()` calls for each controller
  - Manage individual desired onset timing for each controller
- **Playback Controller Responsibility**:
  - Store original onset times from file
  - Receive and apply time ratio adjustments from ensemble controller
- **Timing logic**: 
  - Ensemble controller tracks global playback timer
  - When desired onset time is reached for any controller, calculate ratio for that controller's next onset pair
  - Apply controller-specific ratio to achieve individual target timing

### 4. Playback Flow Algorithm
```
Playback Controller:
1. Load audio file and parse original onset times from file
2. Expose original onset times to ensemble controller
3. Receive and apply time ratio adjustments from ensemble controller

Ensemble Controller:
1. Collect original onset times from all controllers
2. Generate/configure individual desired onset times for each controller
3. Start coordinated playback across all controllers
4. Monitor global playback timer continuously
5. For each controller, when timer >= controller's desired_onset_time[i]:
   - Calculate ratio using controller's onset pair (original[i+1], desired[i+1])
   - Call controller.SetRatioFromOnsetTime(original[i+1], desired[i+1])
   - Increment controller's onset index
6. Repeat until all controllers' onsets processed
```

## New Components to Create

### 1. ARMEOnsetBasedPlaybackController.cs
**Purpose**: Unity component that handles audio playback and stores original onset timing data

**Key Features**:
- Onset file loading and parsing (original audio onset times only)
- Audio playback via existing ARME infrastructure  
- Receives time ratio adjustments from ensemble controller
- Exposes original onset data for ensemble coordination
- Integration with existing audio pipeline

**Inspector Fields**:
- `AudioClip audioClip` - The audio file to stretch
- `TextAsset onsetFile` - .txt file containing original onset times
- `List<float> originalOnsetTimes` - Parsed original onset times (read-only)
- `int currentOnsetIndex` - Current position in onset sequence (managed by ensemble)
- `float currentTimeRatio` - Current applied time ratio (read-only)

**Public API for Ensemble Controller**:
- `List<float> GetOriginalOnsetTimes()` - Access to original onset data
- `void ApplyOnsetTimeRatio(float originalTime, float desiredTime)` - Apply time ratio
- `void SetOnsetIndex(int index)` - Update current onset position

### 2. ARMEOnsetBasedEnsembleController.cs  
**Purpose**: Central coordinator for onset-based time stretching across multiple controllers

**Key Features**:
- **Global playback time tracking** - Master timer for all onset scheduling
- **Individual desired onset management** - Each controller can have unique desired timing
- **Time ratio scheduling** - Calculates and applies time ratios at precise moments
- **Coordinated ensemble control** - Synchronized start/stop/reset operations
- **Future: Audio padding support** - Eventually pad controllers to align first onsets

**Inspector Fields**:
- `List<ARMEOnsetBasedPlaybackController> controllers` - Controllers to manage
- `float defaultDesiredInterval` - Default interval for auto-generation (e.g., 0.3s)
- `List<DesiredOnsetConfig> individualDesiredOnsets` - Per-controller desired timing
- `bool useGlobalTimer` - Whether to use coordinated timing (vs independent)
- `float globalPlaybackTime` - Current ensemble playback time (read-only)
- `UI controls` - Buttons and displays for onset-based control

**Individual Controller Configuration**:
```csharp
[System.Serializable]
public class DesiredOnsetConfig 
{
    public ARMEOnsetBasedPlaybackController controller;
    public List<float> desiredOnsetTimes;  // Individual desired timing
    public float constantInterval;         // For auto-generation (e.g., 0.3s)
    public bool autoGenerate;             // Generate from constant interval
    public float firstOnsetPadding;       // Future: padding time before first onset
}
```

## Implementation Steps

### Phase 1: Core Onset Controller
1. Create ARMEOnsetBasedPlaybackController.cs
2. Implement original onset file parsing in OnValidate()
3. Add public API methods for ensemble controller integration
4. Implement basic audio playback (no time ratio logic yet)

### Phase 2: Ensemble Controller Foundation
1. Create ARMEOnsetBasedEnsembleController.cs
2. Implement global playback timer and controller registration
3. Add individual desired onset time configuration system
4. Implement basic coordinated start/stop functionality

### Phase 3: Dynamic Time Stretching Implementation
1. Add onset scheduling logic to ensemble controller
2. Implement SetRatioFromOnsetTime() calls at precise timing
3. Add per-controller desired onset time management
4. Test with multiple controllers and different desired intervals

### Phase 4: UI Integration and Testing
1. Add UI integration for onset-based controls
2. Test with provided audio file (0.22s average intervals → 0.3s target)
3. Verify timing accuracy and audio quality across multiple controllers
4. Add error handling and validation

### Phase 5: Future Enhancement - Audio Padding
1. Add firstOnsetPadding configuration to DesiredOnsetConfig
2. Implement audio buffer padding in playback controller
3. Add ensemble-level first onset synchronization
4. Test precise real-time onset alignment (e.g., first onset at exactly 1.0s)

## Expected Behavior
- **Input**: Audio with irregular onset timing (e.g., avg 0.22s intervals)
- **Output**: Same audio played with regular onset timing (e.g., exactly 0.3s intervals)
- **Method**: Dynamic time-stretching that adjusts between each onset to achieve target timing
- **Result**: Rhythmically regular playback while preserving pitch and audio quality

## Technical Considerations
- **Timing precision**: Use Unity's AudioSettings.dspTime in ensemble controller for accurate global timing
- **Architecture separation**: Clear responsibility split between controller (audio/data) and ensemble (coordination/timing)
- **Individual flexibility**: Each controller can have completely different desired onset patterns
- **Smoothing**: Consider gradual ratio changes to avoid audio artifacts during transitions
- **Boundary conditions**: Handle end-of-file and onset index boundaries per controller
- **Error handling**: Validate onset files and handle missing/invalid data for each controller
- **Performance**: Monitor CPU usage with multiple controllers and frequent individual ratio changes
- **Future padding**: Design audio buffer management to support sample-level onset alignment

## File Format Examples

### onset_times.txt
```
0.00
0.22
0.43
0.67
0.89
1.12
1.33
```

### Generated Desired Times (0.3s interval)
```
0.0
0.3
0.6
0.9
1.2
1.5
1.8
```

This implementation will provide precise, onset-driven time-stretching that maintains rhythmic regularity while preserving the musical content and pitch of the original audio.