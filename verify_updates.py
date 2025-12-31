import sqlite3
import os

db_path = "tree_kingdoms.db"
names = ['Zhong Hui', 'Sun Quan', 'Sun Ce', 'Sun Jian', 'Lu Meng', 'Lu Zun', 'Xu Shu', 'Diaochan', 'Chen Gong', 'Yuan Shao', 'Hu Ji', 'Lin Zhi', 'Zhao Yun', 'Xiahou Dun']

conn = sqlite3.connect(db_path)
cursor = conn.cursor()

placeholders = ','.join(['?']*len(names))
cursor.execute(f"SELECT name, portrait_source_id, portrait_coords FROM officers WHERE name IN ({placeholders})", names)
rows = cursor.fetchall()

print(f"{'Name':<15} | {'Source':<6} | {'Coords'}")
print("-" * 35)
for r in rows:
    print(f"{r[0]:<15} | {r[1]:<6} | {r[2]}")

conn.close()
