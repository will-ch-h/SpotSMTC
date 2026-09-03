# SpotSMTC

This program simply pipes spotify media from any device (using a developer app) to Window's **System Media Transport Controls** without the need for the spotify app or any other frontend.

for the niche case that you need it :)
## Use

| Short | Long              | Description                                                                                                 |
| :---- | :---------------- | :---------------------------------------------------------------------------------------------------------- |
| -c    | --config \<dir>   | Directory to keep the client id and login in. Nothing is written to disk if omitted.                        |
| -i    | --client-id \<id> | Client ID from Spotify Developer App.                                                                       |
| -j    | --oauth           | Force a fresh browser login, to switch account. Logging in happens on its own when there is no saved login. |
| -h    | --help            |                                                                                                       |