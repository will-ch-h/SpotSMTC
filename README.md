# SpotSMTC

This program simply pipes spotify media from any device (using a developer app) to Window's **System Media Player** without the need for the spotify app or any other heavy frontend.

for the niche case that you need it :)
## Use
Premium is required for playback controls. But the song/artist/album will still be displayed for a free account.

1. Download the appropiate binary from github releases
2. Navigate to the [Spotify Developer Console](https://developer.spotify.com)  
    a. Create a project (Any name and description)  
    b. Set Redirect URI to ```http://127.0.0.1:5000/callback```  
    c. Tick Web API  
    d. Note the Client ID
2. Run with -c -i and -j flags once  
    ``` SpotSMTC.exe -c C:\Path\To\Config -i ClientID -j ```  
    &nbsp; A browser window should open asking to authenticate 
3. Run only with -c afterwards!

If you want to have the service run in the background on startup:  
&nbsp; Add a shortcut in shell:startup where the target is ```C:Path\To\SpotSMTC.exe -c C:\Path\To\ConfigDir```  
&nbsp; You can set the name of the app and icon shown in the media controller by editing the shortcut  

| Short | Long              | Description                                                                                                 |
| :---- | :---------------- | :---------------------------------------------------------------------------------------------------------- |
| -c    | --config \<dir>   | Directory to keep the client id and login in. Nothing is written to disk if omitted.                        |
| -i    | --client-id \<id> | Client ID from Spotify Developer App.                                                                       |
| -j    | --oauth           | Force a fresh browser login, to switch account. Logging in happens on its own when there is no saved login. |
| -h    | --help            |                                                                                                       |

--------------
### Libraries Used/Acknowledgements 
[JohnnyCrazy/SpotifyAPI-NET](https://github.com/JohnnyCrazy/SpotifyAPI-NET)
