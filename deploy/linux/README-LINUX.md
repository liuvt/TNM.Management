# TNM Management v6 · Ubuntu 24.04 / 22.04 (Linux x64)

This bundle includes code changes for Nginx forwarded HTTPS, secure cookie opt-in and persisted data-protection keys. **Neither a live build nor a VPS deployment has been performed in this environment** (no .NET SDK installed here).

## 1. Publish on Windows

At `TNM.Management` project folder (PowerShell):

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\linux\Publish-TNM-Linux.ps1
```

Outputs `tnm-linux-publish.zip`. This updated publish script explicitly includes `deploy/linux` server configs and SQL schema files; verify both are present after extracting the publish ZIP. `App_Data` is excluded on purpose: the VPS creates it on first run, and later updates must never overwrite its database and keyring.

## 2. VPS setup (SSH, Ubuntu)

```bash
sudo apt update
sudo apt install -y aspnetcore-runtime-9.0 nginx certbot python3-certbot-nginx unzip sqlite3 ufw
sudo useradd --system --home-dir /opt/tnm --shell /usr/sbin/nologin tnm || true
sudo mkdir -p /opt/tnm/app /opt/tnm/backups
```

Windows PowerShell (replace IP; use an account with SSH access):

```powershell
scp .\tnm-linux-publish.zip root@YOUR_VPS_IP:/tmp/tnm-linux-publish.zip
```

Ubuntu (run in an SSH shell with sudo):

```bash
sudo unzip -o /tmp/tnm-linux-publish.zip -d /opt/tnm/app
sudo chown -R root:tnm /opt/tnm/app
sudo chown tnm:tnm /opt/tnm/backups
sudo chmod 700 /opt/tnm/backups
sudo find /opt/tnm/app -type d -exec chmod 750 {} +
sudo find /opt/tnm/app -type f -exec chmod 640 {} +
sudo install -d -o tnm -g tnm -m 700 /opt/tnm/app/App_Data
sudo install -m 644 /opt/tnm/app/deploy/linux/tnm.service /etc/systemd/system/tnm.service
sudo systemctl daemon-reload
sudo systemctl enable --now tnm
sudo systemctl status tnm --no-pager
curl -I http://127.0.0.1:5236/account/login
```

## 3. CRITICAL: set a PRIVATE Owner password BEFORE exposing Nginx

The demonstration build creates Owner with a public initial password. The app only listens on local loopback until the Nginx site is enabled. **Before enabling the site**, reset it to a private secret (with an interactive SSH terminal):

```bash
sudo systemctl stop tnm
sudo -u tnm -H bash -c 'cd /opt/tnm/app && exec /usr/bin/dotnet TNM.Management.dll --reset-owner-password'
# Password prompt appears on SSH terminal. Do not pass passwords on the command line.
sudo systemctl start tnm
```

## 4. Configure Nginx + HTTPS

Create a DNS A-record of your desired subdomain pointing to this VPS. Then replace `tnm.example.com` below with the real hostname:

```bash
sudo sed 's/tnm.example.com/tnm.YOURDOMAIN.COM/g' /opt/tnm/app/deploy/linux/nginx-tnm.conf | sudo tee /etc/nginx/sites-available/tnm >/dev/null
sudo ln -s /etc/nginx/sites-available/tnm /etc/nginx/sites-enabled/tnm
sudo nginx -t && sudo systemctl reload nginx
sudo ufw allow OpenSSH
sudo ufw allow 'Nginx Full'
sudo ufw enable
sudo certbot --nginx -d tnm.YOURDOMAIN.COM
```

Visit `https://tnm.YOURDOMAIN.COM/account/login`. Nginx proxies Blazor Server WebSocket connections. Note: Certbot requires a publicly resolvable DNS name and inbound TCP 80/443. If UFW/Cloud firewall has pre-existing rules, review them before enabling UFW.

## 5. Monitoring and backups

```bash
sudo journalctl -u tnm -n 100 --no-pager
sudo systemctl restart tnm
sudo -u tnm -H bash -c 'cd /opt/tnm/app && bash deploy/linux/backup-tnm.sh'
```

Copy backups off-site. Do not copy a live WAL SQLite database as ordinary files; use `sqlite3 .backup` or SQLite Backup API. Keep `/opt/tnm/app/App_Data/keyring` across redeploys so cookie auth keys survive restarts.

## 6. Safe updates

For later updates, preserve `App_Data` and its ownership. Example, after uploading a *new* `tnm-linux-publish.zip` to `/tmp`:

```bash
sudo systemctl stop tnm
sudo -u tnm -H bash -c 'cd /opt/tnm/app && bash deploy/linux/backup-tnm.sh'
sudo unzip -o /tmp/tnm-linux-publish.zip -d /opt/tnm/app
# Chown only application files, never App_Data (tnm-owned)
sudo find /opt/tnm/app -path /opt/tnm/app/App_Data -prune -o -exec chown root:tnm {} +
sudo systemctl start tnm
```

The published ZIP **must not contain App_Data**. Keep the 127.0.0.1-only binding; do not expose Kestrel directly.

## Compatibility note

Project targets net9.0. Microsoft .NET 9 standard support ends Nov 10, 2026; migrate to .NET 10 LTS before long-term production.
