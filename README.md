# SpotSMTC

This program simply pipes spotify media from any device (using a developer app) to Window's **System Media Transport Controls** without the need for the spotify app or any other frontend.

for the niche case that you need it :)
## Use

```
  -c, --config <dir>      directory to keep the client id and login in.
                          Created if it does not exist. Omit it and
                          nothing is written to disk.
  -i, --client-id <id>    client id from your Spotify developer app
  -j, --oauth             force a fresh browser login, to switch account.
                          Logging in happens on its own when there is
                          no saved login.
  -h, --help              this message
```
