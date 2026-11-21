# A Workstream desktop as a container, for development: the same two scripts the machine image is baked
# with, so what runs on a laptop is what runs in Azure, less what a container cannot use (WORKSTREAM_SLIM in
# image/install.sh). Build it from the repository's root:
#
#   docker build -t workstream-desktop:local desktop
#
# The API starts one per task when Workstream:Desktops:Host is docker (api/Desktops/DesktopHosts.cs).
FROM debian:13
# The programs first, in a layer of their own: it changes rarely and takes minutes.
COPY image/install.sh /opt/workstream/image/install.sh
RUN WORKSTREAM_SLIM=1 bash /opt/workstream/image/install.sh
# Then what is Workstream's own, which changes with every change to the agent.
COPY . /opt/workstream
RUN bash /opt/workstream/image/configure.sh
USER work
WORKDIR /home/work
EXPOSE 5900
CMD ["/opt/workstream/start.sh"]
