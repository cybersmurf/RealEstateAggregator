-- Drop OneDrive columns from listings table.
-- OneDrive export was removed in May 2026; Google Drive is now the only cloud target.
-- Idempotent: safe to run multiple times.

ALTER TABLE re_realestate.listings DROP COLUMN IF EXISTS onedrive_folder_id;
ALTER TABLE re_realestate.listings DROP COLUMN IF EXISTS onedrive_inspection_folder_id;
