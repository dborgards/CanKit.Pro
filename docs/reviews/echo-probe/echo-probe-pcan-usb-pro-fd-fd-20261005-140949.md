# Echo probe: pcan-usb-pro-fd-fd

- A (under test): `pcan://PCAN_USBBUS2`; B (peer): `pcan://PCAN_USBBUS1`
- CAN FD, 500000 / 2000000 bit/s
- OS: Microsoft Windows NT 10.0.26200.0; frames per run: 5

| Work mode | Declares Echo | A sees own TX | flagged | unflagged | B sees | SendConfirmedAsync | J1939 claim |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Normal | True | 0/5 | 0 | 0 | 5/5 | confirmed (approx) 6 ms; confirmed (approx) 0 ms; confirmed (approx) 0 ms | skipped |
| Echo | True | 5/5 | 0 | 5 | 5/5 | NOT confirmed (Timeout) 1006 ms; NOT confirmed (Timeout) 999 ms; NOT confirmed (Timeout) 1001 ms | skipped |

Problems reported while measuring (a missing driver or a rejected transmit is not adapter behaviour):
- Echo: SendConfirmedAsync not confirmed: reason Timeout
- Echo: SendConfirmedAsync not confirmed: reason Timeout
- Echo: SendConfirmedAsync not confirmed: reason Timeout

Frames A reported for its own id (first run of each mode):

Normal:
```
  (none)
```

Echo:
```
  +    1 ms  id=0x123  ext=False  IsEcho=False  counter=0
  +   31 ms  id=0x123  ext=False  IsEcho=False  counter=1
  +   61 ms  id=0x123  ext=False  IsEcho=False  counter=2
  +   91 ms  id=0x123  ext=False  IsEcho=False  counter=3
  +  122 ms  id=0x123  ext=False  IsEcho=False  counter=4
```
