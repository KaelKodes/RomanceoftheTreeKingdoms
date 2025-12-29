import sys
import os

# Ensure src is in path
sys.path.append(os.getcwd())

from src.database.db_manager import db
from src.database.models import Faction, UnitType, UnitRole, Officer, City

def seed():
    print("Initializing Database...")
    db.init_db()
    session = db.get_session()
    
    # 1. Clear existing data
    session.query(Officer).delete()
    session.query(City).delete()
    session.query(UnitType).delete()
    session.query(Faction).delete()
    session.commit()
    print("  Cleared old data.")

    # 2. Create Factions
    print("  Creating Factions...")
    loyalists = Faction(name="Imperial Loyalists", color="#0000FF", ideology="Preservation")
    separatists = Faction(name="Separatist Union", color="#FF0000", ideology="Independence")
    coalition = Faction(name="People's Coalition", color="#00FF00", ideology="Equality")
    
    session.add_all([loyalists, separatists, coalition])
    session.commit() # Commit to get IDs

    # 3. Create Unit Types (Roles & Variants)
    print("  Creating Unit Types...")
    units = [
        # Frontline
        UnitType(name="Footman", role=UnitRole.FRONTLINE, is_default=True, 
                 base_health=120, base_attack=15, base_defense=10, description="Standard infantry."),
        UnitType(name="Paladin", role=UnitRole.FRONTLINE, is_default=False, 
                 base_health=150, base_attack=20, base_defense=20, base_mana=50, description="Holy warrior."),
        UnitType(name="Berserker", role=UnitRole.FRONTLINE, is_default=False, 
                 base_health=100, base_attack=35, base_defense=5, description="High damage dealer."),
        
        # Ranged
        UnitType(name="Archer", role=UnitRole.RANGED, is_default=True,
                 base_health=80, base_attack=12, base_defense=2, description="Standard ranged unit."),
        UnitType(name="Fire Mage", role=UnitRole.RANGED, is_default=False,
                 base_health=70, base_attack=25, base_defense=0, base_mana=100, description="AOE magic user."),
        UnitType(name="Sniper", role=UnitRole.RANGED, is_default=False,
                 base_health=80, base_attack=30, base_defense=2, description="High single target damage."),
                 
        # Support
        UnitType(name="Field Medic", role=UnitRole.SUPPORT, is_default=True,
                 base_health=70, base_attack=5, base_defense=5, description="Heals nearby units."),
        UnitType(name="Priest", role=UnitRole.SUPPORT, is_default=False,
                 base_health=60, base_attack=5, base_defense=5, base_mana=100, description="Buffs and heals."),
        UnitType(name="Druid", role=UnitRole.SUPPORT, is_default=False,
                 base_health=80, base_attack=10, base_defense=10, base_mana=80, description="Terrain control."),

        # Cavalry
        UnitType(name="Light Rider", role=UnitRole.CAVALRY, is_default=True,
                 base_health=110, base_attack=18, base_defense=8, base_speed=1.5, description="Fast scount/flanker."),
        UnitType(name="Heavy Lancer", role=UnitRole.CAVALRY, is_default=False,
                 base_health=140, base_attack=25, base_defense=15, base_speed=1.2, description="Shock cavalry."),
        UnitType(name="Horse Archer", role=UnitRole.CAVALRY, is_default=False,
                 base_health=90, base_attack=15, base_defense=5, base_speed=1.6, description="Mobile ranged."),
                 
        # Logistics
        UnitType(name="Supply Cart", role=UnitRole.LOGISTICS, is_default=True,
                 base_health=50, base_attack=0, base_defense=0, description="Carries supplies."),
        UnitType(name="Arcane Mule", role=UnitRole.LOGISTICS, is_default=False,
                 base_health=60, base_attack=0, base_defense=5, base_mana=50, description="Self-sustaining logistics.")
    ]
    session.add_all(units)

    # 4. Create Cities (The Hebei Map) - BEFORE Officers so we can place them
    print("  Creating Hebei-Style Map...")
    cities = {}
    
    def make_city(name, faction=None, econ=100, strat=100, defense=1):
        c = City(name=name, faction=faction, economic_value=econ, strategic_value=strat, defense_level=defense)
        session.add(c)
        session.flush() # Get ID
        cities[name] = c
        return c

    # HQs
    c_capital = make_city("Sun Capital", loyalists, 300, 300, 4) 
    c_ironhold = make_city("Ironhold", separatists, 150, 400, 5)  
    c_eldershade = make_city("Eldershade", coalition, 200, 100, 2)  

    # Key Locations
    make_city("South Fields", coalition, 150, 80, 2) 
    make_city("Tiger Gate", loyalists, 50, 250, 4) 
    make_city("River Port", loyalists, 200, 150, 2) 
    make_city("Twin Peaks", separatists, 80, 200, 3) 
    
    # Neutral/Contested
    make_city("Central Plains", None, 150, 50, 0)
    make_city("West Hills", None, 80, 120, 1)
    make_city("Eastern Bay", None, 120, 80, 1)
    make_city("Mistwood", None, 60, 60, 0) 

    session.commit()

    # 5. Create Officers (Procedural Generation)
    print("  Generating Officers...")
    import random
    
    first_names = ["Caelum", "Thorne", "Elara", "Vael", "Kael", "Lyra", "Roric", "Sylas", "Mara", "Dorn"]
    # last_names = ["Lightbringer", "Ironheart", "Shadowalker", "Oakenshield", "Stormcaller", "Vane", "Blackwood"]
    
    rottk_names = [
        "Cao Cao", "Liu Bei", "Sun Quan", "Lu Bu", "Guan Yu", "Zhang Fei", "Zhao Yun", "Zhuge Liang",
        "Zhou Yu", "Sima Yi", "Yuan Shao", "Dong Zhuo", "Ma Chao", "Huang Zhong", "Sun Ce", "Taishi Ci",
        "Lu Meng", "Lu Su", "Guo Jia", "Xun Yu", "Xiahou Dun", "Xiahou Yuan", "Zhang Liao", "Xu Huang",
        "Dian Wei", "Pang Tong", "Wei Yan", "Jiang Wei", "Deng Ai", "Zhong Hui"
    ]
    random.shuffle(rottk_names) # Ensure distinct names each run
    
    def get_name():
        if rottk_names:
            return rottk_names.pop()
        return f"Generic Officer {random.randint(100, 999)}"
    
    officers = []
    
    # 5a. Create 3 Commanders (Placed in HQs)
    hq_map = {loyalists: c_capital, separatists: c_ironhold, coalition: c_eldershade}
    
    for f in [loyalists, separatists, coalition]:
        name = get_name()
        cmd = Officer(
            name=name, 
            faction=f, 
            location_id=hq_map[f].city_id,
            is_commander=True,
            # High level but variety
            leadership=random.randint(80, 92),
            intelligence=random.randint(80, 92),
            strength=random.randint(80, 92),
            politics=random.randint(75, 88),
            charisma=random.randint(85, 95),
            rank="General",
            reputation=50,
            troops=5000,
            max_troops=5000
        )
        officers.append(cmd)
        
        # 5b. Assign 1-3 Sub-officers to Commander
        for _ in range(random.randint(1, 3)):
            sub = Officer(
                name=get_name(),
                faction=f,
                location_id=hq_map[f].city_id, # Spawn with commander
                leadership=random.randint(20, 75),
                intelligence=random.randint(20, 75),
                strength=random.randint(20, 75),
                politics=random.randint(20, 75),
                charisma=random.randint(30, 80),
                rank="Regular",
                reputation=10,
                troops=1000,
                max_troops=1000
            )
            officers.append(sub)

    # 5c. Create Random Officers (Dispersed)
    all_city_ids = [c.city_id for c in cities.values()]
    
    for i in range(15):
        no = Officer(
            name=get_name(),
            faction=None, 
            location_id=random.choice(all_city_ids), # Random town
            leadership=random.randint(20, 75),
            intelligence=random.randint(20, 75),
            strength=random.randint(20, 75),
            politics=random.randint(20, 75),
            charisma=random.randint(30, 80),
            rank="Volunteer",
            reputation=0,
            troops=random.randint(100, 300),
            max_troops=500
        )
        if random.random() < 0.3: # 30% chance to be already in a faction
            no.faction = random.choice([loyalists, separatists, coalition])
            
        officers.append(no)
        
    session.add_all(officers)
    session.flush()

    # 6. Create Routes (The Web)
    print("  Creating Routes...")
    from src.database.models import Route
    
    session.query(Route).delete() 
    
    def connect(c1_name, c2_name, dist=1.0, rtype="Road", choke=False):
        c1 = cities.get(c1_name)
        c2 = cities.get(c2_name)
        if not c1 or not c2:
            print(f"Error: Missing city for route {c1_name}<->{c2_name}")
            return
            
        session.add(Route(start_city_id=c1.city_id, end_city_id=c2.city_id, distance=dist, route_type=rtype, is_chokepoint=choke))
        session.add(Route(start_city_id=c2.city_id, end_city_id=c1.city_id, distance=dist, route_type=rtype, is_chokepoint=choke))


    # North (Ironhold) -> Central (Sun Capital)
    connect("Ironhold", "Twin Peaks", 2.0, "Mountain Pass", True)
    connect("Twin Peaks", "Tiger Gate", 1.5, "Road", False)
    connect("Tiger Gate", "Sun Capital", 1.0, "Highway", False)
    
    connect("Eldershade", "Mistwood", 1.5, "Forest Path", False)
    connect("Eldershade", "South Fields", 1.0, "Road", False) # Corrected to match image
    connect("Eldershade", "River Port", 1.5, "Road", False) # New Connection
    
    connect("Mistwood", "River Port", 2.0, "River", False)
    connect("River Port", "Sun Capital", 1.0, "Highway", False)
    
    # West/East Flanks
    # connect("Ironhold", "West Hills", 3.0, "Mountain Path", False) # REMOVED: Bypassed Twin Peaks
    connect("Twin Peaks", "West Hills", 2.0, "Mountain Path", False) # Added: Logical flow
    connect("West Hills", "Central Plains", 2.0, "Road", False)
    connect("Central Plains", "Sun Capital", 1.0, "Road", False)
    
    connect("Sun Capital", "Eastern Bay", 2.0, "Road", False)
    connect("Eastern Bay", "River Port", 1.5, "Coast", False)

    session.commit()
    print("Database seeding with Map complete!")

    # 7. Create Battle Templates (Seed Maps)
    print("  Creating Battle Templates...")
    from src.database.models import BattleMapTemplate, BattleNodeTemplate, BattleLinkTemplate
    
    # 7a. Template: Standard Plains
    plains = BattleMapTemplate(name="Standard Plains", terrain_type="Plains", map_type="Open Field")
    session.add(plains)
    session.flush()
    
    # Nodes (Simple 2 Lane MOBA-ish structure)
    # Attackers Left, Defenders Right
    nodes = {}
    
    # Left Side (Attacker)
    nodes["p_hq_blue"] = BattleNodeTemplate(template_id=plains.template_id, name="Blue HQ", x=0, y=50, node_type="HQ", is_attacker_spawn=True)
    nodes["p_cp_b1"] = BattleNodeTemplate(template_id=plains.template_id, name="Forward Camp", x=20, y=50, node_type="Depot")
    
    # Center
    nodes["p_mid"] = BattleNodeTemplate(template_id=plains.template_id, name="Central Field", x=50, y=50, node_type="Standard")
    nodes["p_top"] = BattleNodeTemplate(template_id=plains.template_id, name="North Ridge", x=50, y=80, node_type="Tower")
    nodes["p_bot"] = BattleNodeTemplate(template_id=plains.template_id, name="South Creek", x=50, y=20, node_type="Standard")
    
    # Right Side (Defender)
    nodes["p_cp_r1"] = BattleNodeTemplate(template_id=plains.template_id, name="Outer Guard", x=80, y=50, node_type="Depot")
    nodes["p_hq_red"] = BattleNodeTemplate(template_id=plains.template_id, name="Red HQ", x=100, y=50, node_type="HQ", is_defender_spawn=True)
    
    for k, n in nodes.items():
        session.add(n)
        
    session.flush() # get IDs
    
    # Links
    def link_nodes(n1_key, n2_key, dist=1.0):
        session.add(BattleLinkTemplate(template_id=plains.template_id, source_node_id=nodes[n1_key].node_id, target_node_id=nodes[n2_key].node_id, distance=dist))

    # Mid Lane
    link_nodes("p_hq_blue", "p_cp_b1")
    link_nodes("p_cp_b1", "p_mid")
    link_nodes("p_mid", "p_cp_r1")
    link_nodes("p_cp_r1", "p_hq_red")
    
    # Flanks
    link_nodes("p_cp_b1", "p_top")
    link_nodes("p_top", "p_cp_r1")
    
    link_nodes("p_cp_b1", "p_bot")
    link_nodes("p_bot", "p_cp_r1")
    
    session.commit()
    print("Database seeding with Battle Templates complete!")

if __name__ == "__main__":
    seed()

