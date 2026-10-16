FROM python:3.13-alpine
COPY remsound-relay.py /usr/local/bin/remsound-relay.py
RUN install -d -o nobody -m 750 /var/log/remsound-relay
USER nobody
EXPOSE 47830/udp
ENTRYPOINT ["python3", "/usr/local/bin/remsound-relay.py"]
