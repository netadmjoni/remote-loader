- Purpose:

Continuously poll the Cisco WGB (C9167) once per second and show where it is associated (Parent AP) + signal level (RSSI).

- Requirements (Linux / WSL):
 
`sudo apt-get update`
`sudo apt-get install -y expect`


- Run (interactive login + enable):

`./wgb_watch.exp <WGB_IP> <USERNAME> "<LOGIN_PASSWORD>" "<ENABLE_PASSWORD>" [interval_seconds]`

- Example:

`./wgb_watch.exp 10.194.240.11 admin "MyLoginPass" "MyEnablePass" 1`

- Save output to a logfile:

`./wgb_watch.exp 10.194.240.11 admin "MyLoginPass" "MyEnablePass" 1 | tee wgb_assoc.log`
