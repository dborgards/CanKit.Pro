# Echo probe: kvaser

- A (under test): `kvaser://0`; B (peer): `pcan://PCAN_USBBUS1`
- Classic CAN, 500000 bit/s
- OS: Microsoft Windows NT 10.0.26200.0; frames per run: 5

| Work mode | Declares Echo | A sees own TX | flagged | unflagged | B sees | SendConfirmedAsync | J1939 claim |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Normal | True | 0/5 | 0 | 0 | 5/5 | confirmed (approx) 2 ms; confirmed (approx) 0 ms; confirmed (approx) 0 ms | Claimed 0x11 (289 ms, peer saw 1) |
| Echo | True | 0/5 | 0 | 0 | 5/5 | NOT confirmed (Timeout) 1004 ms; NOT confirmed (Timeout) 1000 ms; NOT confirmed (Timeout) 1000 ms | NotClaimed  (1005 ms, peer saw 1) error: J1939NodeException: J1939 address claim TX failed (id=0x18EEFF11): Timeout. |

Problems reported while measuring (a missing driver or a rejected transmit is not adapter behaviour):
- Echo: SendConfirmedAsync not confirmed: reason Timeout
- Echo: SendConfirmedAsync not confirmed: reason Timeout
- Echo: SendConfirmedAsync not confirmed: reason Timeout
- Echo: J1939 claim: J1939NodeException: J1939 address claim TX failed (id=0x18EEFF11): Timeout.

Frames A reported for its own id (first run of each mode):

Normal:
```
  (none)
```

Echo:
```
  (none)
```
