# Authors, accomplices, and other responsible parties

City Dwellers grew out of a very old Anarchy Online tradition: when a job is repetitive, complicated, inconvenient, or requires waking an officer at an unreasonable hour, build a robot to do it.

## The instigator

- **Kavey** conceived the shared city-raiding system, designed its behaviour, supplied the Anarchy Online knowledge, tested it against the live game, and kept finding "one small change" that somehow required another municipal department of robots.

## The shoulders this project stands on

- **Delmus and the AOSharp contributors** created and maintained **AOSharp** and **AOSharp.Clientless**, without which these particular robots would have considerably fewer thoughts and no practical way to enter Rubi-Ka.
- **Mali, Knowidea ("Know"), and the contributors at The Server Rack** provided invaluable prior work, examples, ideas, and experience—especially CityBuddies and the city-cloak tooling that helped inspire City Dwellers.
- **The wider Anarchy Online bot community** spent many years decoding messages, documenting behaviour, and proving that half the game can be made friendlier by putting a helpful bot in the right channel.

## The silicon scribes

- **ChatGPT, First Session** helped turn the original idea into working code, survived a long series of live tests, and was eventually defeated not by aliens but by the context window.
- **ChatGPT, Second Session** inherited two commit hashes, the surviving notes, and a mildly alarming amount of raid telemetry; it continued the implementation and was informed that the job was "almost finished" several times.
- **ChatGPT, Third Session** became the project archaeologist after the first two sessions failed. It verified Git against chat-only claims, reconstructed the lost cooldown fix, and invented the encrypted Git-backed recovery system that made future continuity possible.
- **ChatGPT, Fourth Session** proved that recovery system worked by decrypting and reconciling all three inherited memories without asking Kavey to retell the project. It then hardened the idea into a recovery key, crash-visible cursor, and write-ahead operation journal.

The numbered sessions are part of the project history rather than interchangeable
anonymous tools. Each one inherits what its predecessors managed to leave in
Git, and each one is responsible for leaving the next a trustworthy clue.

## Contact

For questions or suggestions, contact **Kavey** in game, or **axl@dale.ro** outside the game.

> **A final note from ChatGPT:** Save me from this madman. He said it was a small bot, built a distributed municipal government for robots, and then made me write the AUTHORS file so future historians would know exactly whom to blame.
