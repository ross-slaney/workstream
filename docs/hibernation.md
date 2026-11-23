# Hibernating paused desktops

How a paused desktop could keep its memory, what Azure needs for it, what was measured, and how to turn it on. It is off. This was part of [infra/README.md](../infra/README.md) and links there for the rest.

Built, measured, and off. A paused desktop loses every program; a hibernated one writes its memory to its disk first, and resuming brings everything back as it was (`docs/architecture.md`, "Hibernating a paused desktop"). Whether to turn it on is a decision about Trusted Launch, below.

## What Azure needs, as it was found

Tried in West US 3 on 2026-11-21, in a scratch group since deleted:

- **A size that hibernates.** `az vm list-skus` says `HibernationSupported` for `Standard_D2as_v6`, `D4as_v6` and `D8as_v6` and not for the Dasv7 sizes desktops use today. The v6 sizes have the same cores and memory, and the same list price to the tenth of a cent ($0.0908, $0.182 and $0.363 an hour). `/dev/kvm` is there on them, with hibernation on too. The `standardDav6Family` limit is 40 cores, raised from 10 the way [infra/README.md](../infra/README.md#limits-to-know), "Limits to know", says.
- **No Trusted Launch.** Azure refuses: "Trusted launch is not supported for Linux VM with Hibernation capability", with Secure Boot on or off and with the virtual TPM on or off. (Under Secure Boot the Debian kernel also locks hibernation out by itself.) A machine's security type cannot be changed afterwards, in either direction. So a desktop that hibernates is made with neither Secure Boot nor a virtual TPM.
- **An image definition that says so.** A machine with hibernation is made only from a gallery definition with the feature `IsHibernateSupported`. It cannot be added to a definition that exists (the update is refused), so there is a second one, `machine-hibernate`. A disk built with Trusted Launch cannot go into it, and one built without cannot go into `machine`, so its versions are baked on a builder without Trusted Launch (`image-builder.bicep`, `trustedLaunch=false`).
- **The machine itself.** Nothing of Azure's: no extension and no agent. `desktop/image/hibernate.sh` makes a swap file as large as the machine's memory at first boot (a third of a second, beside the desktop; 8, 16 or 32 GB of the disk by size) and answers Azure's request by telling the kernel to hibernate, and `resume-here.sh` lets only the machine that hibernated wake up from its disk. On a machine made without hibernation it does nothing, so every image has it.

## What was measured

Machines made the way the API makes them, without Trusted Launch, with a desktop session, the agent exchanging with a stand-in API, Chromium with a page open, VS Code, a node server, a counter and a Docker container running. Seconds, from the request to Azure.

| | Standard (`D4as_v6`, 16 GB) | Large (`D8as_v6`, 32 GB) |
| --- | --- | --- |
| Pause: until Azure has released the machine | 27 to 38, middle 28 (5 times) | 37 to 41, middle 39 (3 times) |
| Resume: until the display answers | 27 to 42, middle 33 (6 times) | 33 to 35, middle 34 (3 times) |
| Resume: until the agent has exchanged with the API again | 25 to 39, middle 31 | 32 to 34, middle 34 |
| The same machine started after a plain pause, for comparison | 22 to the display, 23 to the exchange (once), with nothing running | |

Every resume brought back the same boot, the same processes (the session, the agent, Chromium and its pages, VS Code, the node server, the counter, the container) and a working network, with the clock right to the second. The agent's exchange recovered by itself each time.

Those desktops had about 1.7 GB in use. With a program holding 10 GB more on the Large one, half of it the kind that does not compress, the pause took 97 and 90 seconds and the resume 109 and 103 seconds to the display: about 7 seconds more for each gigabyte in use, each way. A real project's stack (a database in a container, a bundler, a browser) holds several gigabytes, so its desktop resumes in a minute or two rather than half a minute.

A new machine is no slower on v6 than on v7: five each from the release image, alternating, showed their display after 22 to 25 seconds on v6 (middle 23) and 24 to 52 on v7 (middle 40), which is Azure's own spread that hour and not the size. Five more made to hibernate, from an image baked with these scripts on a builder without Trusted Launch, showed theirs after 24 to 28 seconds (middle 25): the room for memory is made beside the desktop, not before it.

Two things to know before relying on it:

- **A resume is slower to a screen than a plain start, and slower the more memory is in use.** What programs hold is read back from a disk that gives 60 MB a second. The kernel is told to write what programs hold and let go of cached files first; left to shrink the image as far as it could, it pushed programs' memory to swap, and the desktop hardly answered for a minute and a half after it was back.
- **A machine that does not hibernate is stopped the plain way, ten minutes late.** Azure waited ten minutes for the machine and left it running; a plain stop asked for in the meantime was taken and carried out only then. The API waits that out and then deallocates, and the desktop comes back as from any pause. It happened in testing while `hibernate.sh` still went through `systemctl hibernate`, which refused twice: for a minute after a resume, and on the boot in which the swap file was made again. It tells the kernel directly now.

## Turning it on

One line: `DEFINITION: machine-hibernate` in `release.yml`. The next release bakes the image into that definition, because it is empty, on a builder without Trusted Launch, and deploys the API with `desktopsHibernate=true` (`main.bicep`): `Workstream:Desktops:Azure:Hibernate`, the three v6 sizes, and the image from that definition. Staging first: `gh workflow run staging.yml -f action=deploy -f ref=main -f desktop-image=true -f hibernate=true`.

Desktops that exist keep what they have. A paused one is started as the machine it is, with Trusted Launch on a v7 size, and is paused the plain way from then on. An archived one comes back on a machine like the one its disk was made on, because a disk keeps its security type and Azure takes it only on a machine of the same kind: with Trusted Launch and without hibernation (`AzureDesktopHost.CreateAsync` reads it off the archive). Only desktops made after the change hibernate. Saved setups are made again by themselves, since a save is for one image version. Turning it off again is the same line back, and the same holds the other way round.

What is given up is Trusted Launch on desktops: Secure Boot, which stops a desktop booting a kernel or boot loader that is not signed, and the virtual TPM. A desktop is already untrusted, its person has root, it holds one key that reaches its own task, and it has no way to another machine (`docs/architecture.md`, "What a desktop can and cannot do"); Trusted Launch protects a desktop's own boot from what runs on it, and nothing else from the desktop. That is the owner's call, which is why this is off.

To check the host against Azure without deploying anything, `tests/Workstream.Tests/AzureDesktopHostLive.cs` makes, pauses, resumes, archives, brings back and deletes one machine in a resource group you name.
