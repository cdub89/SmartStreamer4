# Code Signing

Every SmartStreamer4 build is Authenticode-signed: tester previews and
published releases alike (operator decision, 2026-09-20). There is no
unsigned fallback and no skip flag. `publish-release.ps1` refuses to run
if it cannot sign, and refuses to package an exe whose signature does not
check out.

Signing is done through **Azure Artifact Signing** with
[jsign](https://ebourg.github.io/jsign/), the same account, certificate
profile and recipe the SKCCLogger project uses from its Linux build box.
The Azure half (subscription, identity validation, certificate profile,
app registration, roles, costs, renewals) is documented once, in
SKCCLogger's `CODE-SIGNING.md`, under "Windows". Nothing on the Azure side
is specific to an application, so nothing there changed for this project.
This file covers only what the Windows seat needs.

The publisher shown to operators is the validated individual's legal name,
because the identity validation is for an individual.

## What runs, in order

All in `publish-release.ps1`, for both `-Preview` and `-Publish`.

1. **Preflight**, before the tests: reads the config, refuses a missing
   key or an expired client secret (warns inside 30 days), checks that
   `java` and the jsign jar are present, and obtains a token so a bad
   secret fails in seconds rather than after the build.
2. **Sign**, after the embedded-version check and before the zip: a fresh
   token, then jsign against the account's endpoint and profile, with
   Microsoft's timestamp server named explicitly. The token is in the
   environment only for the life of the jsign call, so `wrangler` and
   `gh` further down never inherit it, and it is never on a command line.
3. **Verify**, natively: `Get-AuthenticodeSignature` must report `Valid`,
   a timestamp must be present, the signer's Common Name must equal
   `expected_cn` **exactly**, and the issuer must be a Microsoft CA. Any
   miss aborts with no zip and nothing shipped.
4. The zip and its SHA256 are made after signing, so they cover the signed
   exe.

Why the exact name and the issuer: `Valid` only means the chain is trusted
by this PC, which a local test root would also satisfy. The name and the
issuer are what prove who signed. Why the timestamp is mandatory: signing
certificates from this service live 72 hours, so an exe without one stops
validating three days after release.

Verification needs no `osslsigncode` and no vendored root certificate on
Windows, because Windows already trusts Microsoft's signing root. That is
the one deliberate difference from the Linux seat.

## Files on the Windows seat

All under `%USERPROFILE%\.skcclogger-signing\windows\`, outside every repo.

| File | What it is |
| --- | --- |
| `azure.conf` | `key=value` credentials, same layout as the Linux seat. Readable by the owner only. |
| `jsign-7.5.jar` | The signer. The name is pinned in the script on purpose. |

`azure.conf` keys: `tenant_id`, `client_id`, `client_secret`,
`client_secret_expires` (optional, `YYYY-MM-DD`), `endpoint`, `account`,
`profile`, `expected_cn`. Every value except the secret and its expiry is
identical to the Linux seat's file, and none of those is a secret.

Java is needed for jsign. Any current JDK or JRE on the `PATH` will do;
this seat uses Microsoft's OpenJDK build.

## Setting up a Windows seat

1. Install Java and confirm `java -version` works in a **new** terminal.
2. Download `jsign-7.5.jar` from the jsign releases page into the folder
   above. To move to a newer jsign, change the pinned name in
   `publish-release.ps1` in the same commit.
3. Create a client secret **for this PC** on the existing app
   registration: Azure portal, App registrations, the signing app,
   Certificates & secrets, New client secret, longest expiry offered.
   Copy the **Value** at once; it is shown only at creation. The Secret ID
   beside it is not the secret. One secret per machine, so either can be
   revoked without touching the other.
4. Create `azure.conf` with the keys above, then restrict it:
   `icacls <file> /inheritance:r /grant:r "<user>:(R,W)"`.
5. The next tagged run proves it: the preflight prints
   `Config, java, jsign, token: OK` within seconds, before the tests
   start. A wrong secret, a missing jar or a missing `java` is refused at
   that point, with nothing built.

Never paste the secret into a chat, an issue or a commit. Nothing in the
script prints it, or a token.

## Checking a signed build

Right-click the exe, Properties, Digital Signatures: the signer and a
timestamp should be listed. From PowerShell:

```powershell
Get-AuthenticodeSignature .\SmartStreamer4.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
```

## Maintenance

- **The client secret expires** (24 months at most). This seat's was
  created 2026-09-20. The preflight warns for 30 days, then refuses to
  build, so a release cannot go out unsigned by accident. Create a new one
  as in step 3 and update both lines in `azure.conf`.
- **Identity validation expires** and is renewed once for every project;
  see SKCCLogger's document. Without it the service stops issuing
  certificates and signing fails.
- **SmartScreen** reputation builds with downloads, whoever the signer is.
  Signing replaces "unknown publisher" at once; a caution on first
  downloads can persist for a while. Say so when handing a build to
  testers.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `invalid_client` at the preflight | Wrong or expired client secret, or the Secret ID was pasted instead of the Value. |
| jsign fails with HTTP 403 | The app registration lacks the *Certificate Profile Signer* role on the signing account. |
| `java is not on the PATH` | Java was installed after the terminal opened. Open a new one. |
| Signature check says the signer is someone else | Wrong `profile`, or `expected_cn` does not match the profile's subject exactly. |
| Signature is `Valid` but there is no timestamp | The timestamp server was unreachable. Re-run; never ship without one. |

## Re-signing an artifact by hand

Done once, for `v0.3.3-preview2`, which was uploaded before signing
existed: the exe was taken out of the shipped zip, signed and verified
with the same jsign command the script uses, zipped again under the same
name, and uploaded over the original, along with a matching checksum file.
Testers had not yet been given the link. This is not a routine: a build
made by the current script is already signed.
