# RemSound v5.9

The audio latency control now works properly while you're listening.

On most setups, moving the latency control while sound was playing did nothing at all — neither up nor down. It only ever took effect if you set it before connecting, which made it look like the setting was simply ignored.

Two separate things were wrong, and both are fixed:

- The control was sending its value somewhere the audio never looked, so the receiving side quietly stayed on its starting value for the whole session.
- Even once that reached the right place, the change crept in so slowly that a large move took over two minutes to arrive — still indistinguishable from nothing happening.

Move it now and the delay follows within a few seconds. There's no gap and no click while it changes. Lowering takes effect straight away; raising can't be instant, because the extra cushion has to be built out of the sound still arriving, so RemSound plays very slightly slow for a moment while it banks the difference — you can hear it stretch, and that's the change happening.

Automatic latency tuning was affected by the same fault, so it now takes effect too.

Setups using separate WASAPI and ASIO latency controls were the one case that already worked. They keep their two independent settings, unchanged.
