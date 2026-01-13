# TapoToggle
Small app to permit toggling Tapo devices - lights, switches etc

To use: 

- Run the program with no arguments and you'll be prompted for your Tapo credentials; you can then select from your list of devices known in the cloud and toggle the one you wish.

_ALTERNATIVELY..._

- Create a config file for the devices you want to control, e.g. BedroomLight.json (model it on the config.demo.json file provided)
- Ensure the app is built
- Run the app with the config file as an argument:
  - `dotnet run BedroomLight`

Whichever approach you take, the device will have its state toggled, i.e. bulbs will turn on if off, sockets will turn off if on.
