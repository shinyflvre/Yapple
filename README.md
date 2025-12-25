<img width="1442" height="778" alt="YappleBG" src="https://github.com/user-attachments/assets/37206b6c-5b2a-4321-83c6-188fdb5d173b" />

# Yapple

**Yapple** is a lightweight **offline Speech-to-Text voice command app** that runs actions based on trigger words or trigger sentences you speak.
It works **without an internet connection** and is optimized for **fast trigger response times**.

You can use **four different command types**:

* Soundboards
* Windows Commands
* VRChat OSC
* Keybinds

# Resource Frindly
<img width="576" height="120" alt="image" src="https://github.com/user-attachments/assets/5031c405-3e2e-4aff-b396-524bd914ad1a" />


---

## 1. Soundboards

<img width="468" height="187" alt="image" src="https://github.com/user-attachments/assets/a35c62f6-06cf-4c43-91c4-d54157280111" />

With **Add Sound**, you can add:

* `.mp3`
* `.ogg` (Vorbis only)
* `.wav`

After adding a sound file, you can assign a **trigger word or trigger sentence**
(minimum **1 word**, maximum **4 words**).

**Example:**
If the trigger word is **“Workout”** and the file is `WORKOUT.wav`, saying *“Workout”* will play the sound.

You can combine Yapple with tools like **Voicemeeter**, allowing others in **Discord, VRChat, or games** to hear the sounds.

### Stop Sound (System Command)

Yapple includes a customizable **System Stop Command** as a safety feature.

If you play a long sound (e.g. 40 seconds), you can say:

> **“Stop Sound”**

This will immediately stop the currently playing audio.

---

## 2. Commands (Windows CMD)

<img width="469" height="181" alt="image" src="https://github.com/user-attachments/assets/4d6fed57-f838-4310-b481-f5a93fabef8e" />

With **Add CMD**, you can run standard **Windows commands**.

**Example:**

* Trigger: **“Open Netflix”**
* Command:

  ```
  start firefox https://www.netflix.com/browse
  ```

Saying *“Open Netflix”* will launch **Mozilla Firefox** and open Netflix automatically.

You can also:

* Open applications
* Run scripts
* Manage files

⚠ **Warning**
Only use **safe commands**.
Avoid dangerous or destructive commands that could be accidentally triggered by speech recognition.

---

## 3. VRChat OSC

<img width="471" height="177" alt="image" src="https://github.com/user-attachments/assets/7e6922fb-d758-467c-ba3d-7e64e4cb61d0" />

With **VRC OSC**, you can control **Bool parameters** on your VRChat avatar’s Animator.

**Example:**

* Trigger: **“Do A Dance”**
* Parameter:

  ```
  toggle:VRCEmoteManagerRand:true
  ```

When you say *“Do A Dance”*, your avatar will play a random dance animation.

You can use this for:

* Clothing toggles
* Weapons
* Particles
* Any bool-based behavior

### Prefix System

#### Normal

```
UseWeapon:true
UseWeapon:false
```

#### Toggle

```
toggle:UseWeapon:true
```

Toggles the value each time it is triggered.

#### Trigger (for Contacts)

```
trigger:UseWeapon:true
```

Sets the parameter to `true` for **~1–1.5 seconds**, then automatically resets it to `false`.

This is required for **VRChat Contacts**, which trigger local bools after ~0.75–1.5 seconds.

---

## 4. Keybinds

<img width="468" height="184" alt="image" src="https://github.com/user-attachments/assets/d41f2523-c347-4cf5-939d-3d7f64a58597" />

With **Add Key**, you can assign **keyboard shortcuts** to voice commands.

**Example:**

* Trigger: **“Clip Yes”**
* Keybind:

  ```
  WIN + ALT + G
  ```

Saying *“Clip Yes”* will trigger the **Windows 30-second clip recording** feature.

⚠ **Important**

* Do **not** press any keys while the voice command is executing
* Pressing keys at the same time may cause Windows to misfire the shortcut

---
