FROM ubuntu:22.04
WORKDIR /game
COPY . .
RUN chmod +x /game/SpellHaul-LinuxServer.x86_64
RUN apt-get update && apt-get install -y --reinstall ca-certificates
CMD ["/game/SpellHaul-LinuxServer.x86_64", "-server", "-batchmode", "-nographics", "-logfile", "-"]
