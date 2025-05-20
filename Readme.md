## CookieMonster Prototype

This cookie monster, although it is named as 'FirefoxCookieReader', finds cookies for many other browsers. It is still under developement.

### How does it Work?

This code only works on Windows Machines. It looks for cookie related SQLite databases through the powershell, pipes the output to check the directories for cookies. For Firefox it looks for the cookies.sqlite file and for chromium based browsers it looks for the Cookie file. Then it performs a querry on it to find the cookies.

To run it just start the executable given in the repo.

### Issues to Fix:
 - There are some issues with decryption as some browsers encrpyt their cookies, the encrypted cookies are shown as `[encrypted]
 ` as of now,
 - There are some issues regarding Chrome's cookies, for some reason it cannot find those cookies, it most likely stems from the Powershell command.

 ### What I would like to achieve in the future
 - I want it to send the cookies it finds to a server that runs on my computer so that it can actually count as malware