# Database backups for the gateway (README, "Database backups"): PostgreSQL's own client tools at the server's
# version, age to encrypt each backup to a public key whose private half stays off the server, and rclone for an
# optional off-site copy. The base image is pinned with the database's (docker-compose.yml); raise them together.
ARG BASE_IMAGE=postgres:17.6
FROM ${BASE_IMAGE}

RUN apt-get update \
 && apt-get install --yes --no-install-recommends age rclone ca-certificates \
 && rm -rf /var/lib/apt/lists/*

COPY gateway-backup gateway-backup-loop gateway-backup-health gateway-restore /usr/local/bin/
COPY verify.sql /usr/local/share/gateway-backup/verify.sql

# Not the database server: this image only runs the backup schedule.
ENTRYPOINT []
CMD ["gateway-backup-loop"]
