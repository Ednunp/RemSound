Old-file fixtures for the gate step "OLD FILES" (SelfTest.OldFiles.cs), made 2026-09-28.

What it does: builds a released version's own RemSound.Core from its git tag and writes a profile and a global config
with every field set to something other than its default, using that version's own serializer settings.

To add a new release (say v6.1) after it ships:
  1. git archive --format=tar v6.1 src/RemSound.Core -o old.tar, and unpack it into a folder OLD (tar --force-local -xf).
  2. Copy this folder next to it; in gen.csproj replace OLD-CORE with the path to OLD, and in DefineConstants use GEN_v6_1
     (add a matching #if in Gen.cs for the version string).
  3. dotnet run -c Release -- <output folder>
  4. Paste the two JSON files into SelfTest.OldFiles.cs as V61Profile / V61GlobalConfig and add them to the loop.
Keys a later version stops reading go into RetiredOldProfileKeys / RetiredOldConfigKeys WITH a reason; the step checks
each listed key really is gone from today's classes.
