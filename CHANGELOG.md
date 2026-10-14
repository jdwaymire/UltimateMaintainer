# Changelog

## 1.0.0
- First public release. Three maintenance jobs, each off until switched on, with its own settings section, Preview,
  Run now and daily scheduled task:
  - **Partial watch cleanup**: items left partly watched for longer than a chosen number of days are marked watched
    or have their resume point cleared. Items watched before keep their watched mark. Optional: items with no play
    date too. Undo of the last run.
  - **Next episode cleanup**: hides the next-episode entry of series not played for a chosen number of days from
    Continue Watching (Emby's own "Hide from Continue Watching"); nothing is marked watched. Undo of the last run.
  - **Old devices cleanup**: removes devices not seen for a chosen number of days (at least 7); devices in use are
    never removed.
- All users or chosen users per job; chosen libraries for the Continue Watching jobs. Own log and run records in the
  plugin's data folder.
