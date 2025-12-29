import pandas as pd
import openpyxl
import re

INPUT_FILE = 'TreeKingdoms.xlsx'
OUTPUT_FILE = 'TreeKingdoms_Cleaned.xlsx'

def extract_tables(file_path):
    print(f"Reading {file_path}...")
    xl = pd.ExcelFile(file_path)
    tables = {}

    for sheet_name in xl.sheet_names:
        print(f"Parsing sheet: {sheet_name}")
        df = xl.parse(sheet_name, header=None)
        
        # Iterate rows manually to identify tables by "Column Name" header
        row_idx = 0
        while row_idx < len(df):
            row = df.iloc[row_idx]
            # Convert to string and check for header
            row_str = " ".join(row.astype(str).values)
            
            if "Column Name" in row_str and "Type" in row_str:
                # Found a table header
                print(f"  Found table header at row {row_idx}")
                
                # Determine Table Name (Look backwards for a title)
                # Heuristic: Scan upwards from row_idx-1.
                # Blocks are separated by empty lines.
                # Block 1 (closest to table) is usually Description.
                # Block 2 (above Block 1) is usually Title.
                
                scan_k = row_idx - 1
                title = f"Table_{row_idx}"
                
                # We attempt to find up to 2 blocks.
                # If the first block is short / title-like, use it.
                # If it is long / description-like, look for the block above it.
                
                def find_block_above(start_k):
                    # 1. Skip empty lines going up
                    k = start_k
                    while k >= 0:
                        val = df.iloc[k, 0]
                        if pd.notna(val) and str(val).strip() != "" and str(val).strip().lower() != 'nan':
                            break
                        k -= 1
                    
                    if k < 0: return None, -1
                    
                    # 2. Find top of this block
                    bottom = k
                    top = k
                    while top >= 0:
                        val = df.iloc[top, 0]
                        if pd.isna(val) or str(val).strip() == "" or str(val).strip().lower() == 'nan':
                            top += 1
                            break
                        top -= 1
                    if top < 0: top = 0
                    
                    # Return content of the FIRST line of this block, and the new scan position (top-1)
                    val = df.iloc[top, 0]
                    return str(val).strip(), top - 1

                # Try finding first block
                block1_text, next_scan = find_block_above(scan_k)
                if block1_text:
                    # Check if Description-like
                    is_desc = False
                    if len(block1_text) > 40 or block1_text.startswith("Defines ") or block1_text.startswith("Tracks ") or block1_text.endswith("."):
                        is_desc = True
                    
                    if is_desc:
                        # Look for block above
                        block2_text, _ = find_block_above(next_scan)
                        if block2_text:
                            title = block2_text
                        else:
                            # Fallback: maybe the description is all we have? 
                            # But prefer mapped name using the Description start?
                            title = block1_text 
                    else:
                        title = block1_text




                print(f"    Title detected: {title}")

                # Extract content
                header = row.values
                # Sanitize header
                header = [str(h) if pd.notna(h) else f"Unnamed_{i}" for i, h in enumerate(header)]
                
                data_rows = []
                r_scan = row_idx + 1
                
                while r_scan < len(df):
                    scan_row = df.iloc[r_scan]
                    scan_str = " ".join(scan_row.astype(str).values)
                    
                    # Stop if next header found
                    if "Column Name" in scan_str and "Type" in scan_str:
                        break
                    
                    data_rows.append(scan_row.values)
                    r_scan += 1
                
                # Create DataFrame
                if data_rows:
                    table_df = pd.DataFrame(data_rows, columns=header)
                    table_df = table_df.dropna(how='all')
                    tables[title] = table_df
                
                row_idx = r_scan
            else:
                row_idx += 1
            
    return tables

def clean_and_save(tables):
    print("Organizing tables...")
    
    # Identify Units and UnitTypes keys
    units_key = None
    unit_types_key = None
    
    # We need to find the specific Titles we detected
    for k in tables.keys():
        if k == 'Units':
            units_key = k
        if 'UnitTypes' in k:
            unit_types_key = k
            
    merged_units = pd.DataFrame()
    if units_key and unit_types_key:
        print(f"  Merging '{units_key}' and '{unit_types_key}'...")
        df1 = tables[units_key]
        df2 = tables[unit_types_key]
        
        merged_units = pd.concat([df1, df2], ignore_index=True)
        
        col_name_header = [c for c in merged_units.columns if 'Column Name' in str(c)]
        if col_name_header:
            cname = col_name_header[0]
            merged_units = merged_units.drop_duplicates(subset=[cname])
        
        del tables[units_key]
        del tables[unit_types_key]
    elif units_key:
        merged_units = tables.pop(units_key)
    elif unit_types_key:
        merged_units = tables.pop(unit_types_key)

    # Name Mapping
    name_map = {
        'UnitStats': 'Unit Stats',
        'UnitBehaviorStates': 'Unit Behavior',
        'OfficerTypes': 'Officers',
        'SupplySources': 'Logistics',
        'MoraleStates': 'Morale States',
        'ControlPointAdjacency': 'CP Adjacency',
        'ControlPointStatus': 'CP Status',
        'CaptureRules': 'Capture Rules',
        'ZonePressure': 'Zone Pressure',
        'HQRules': 'HQ Rules',
        'BattleVisibility': 'Battle Visibility',
        'BattleEndConditions': 'End Conditions',
        'EngagementStates': 'Engagement States',
        'EngagementParticipants': 'Combat Participants',
        'DisengagementRules': 'Disengagement Rules',
        'CasualtyConversion': 'Casualty Rules',
        'CombatOutcomes': 'Combat Outcomes',
        'PursuitRules': 'Pursuit Rules',
        'CombatPhases': 'Combat Phases',
        'Units': 'Unit Definitions',
        'BattleMaps': 'Control Points'
    }

    print(f"Writing to {OUTPUT_FILE}...")
    with pd.ExcelWriter(OUTPUT_FILE, engine='openpyxl') as writer:
        if not merged_units.empty:
            merged_units.to_excel(writer, sheet_name='Unit Definitions', index=False)
            
        for name, df in tables.items():
            sheet_name = name
            
            if '(' in sheet_name:
                sheet_name = sheet_name.split('(')[0].strip()
                
            best_match = None
            for key, val in name_map.items():
                if key == sheet_name:
                    best_match = val
                    break
                if key in sheet_name and not best_match:
                    best_match = val
            
            if best_match:
                sheet_name = best_match
            
            invalid_chars = '[]:*?/\\'
            for c in invalid_chars:
                sheet_name = sheet_name.replace(c, '')
            sheet_name = sheet_name[:31]
            
            try:
                df.to_excel(writer, sheet_name=sheet_name, index=False)
                print(f"  Saved sheet: {sheet_name}")
            except ValueError:
                alt_name = sheet_name[:28] + "_1"
                df.to_excel(writer, sheet_name=alt_name, index=False)
                print(f"  Saved sheet: {alt_name} (renamed from {sheet_name})")

if __name__ == "__main__":
    tables = extract_tables(INPUT_FILE)
    clean_and_save(tables)
    print("Done processing.")
