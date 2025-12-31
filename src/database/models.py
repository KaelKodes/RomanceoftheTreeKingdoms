from sqlalchemy import Column, Integer, String, Float, Boolean, ForeignKey, Enum as SqEnum
from sqlalchemy.orm import relationship, declarative_base
import enum

Base = declarative_base()

# Enums
class UnitRole(enum.Enum):
    FRONTLINE = "Frontline"
    RANGED = "Ranged"
    SUPPORT = "Support"
    CAVALRY = "Cavalry"
    LOGISTICS = "Logistics"

class Faction(Base):
    __tablename__ = 'factions'
    
    faction_id = Column(Integer, primary_key=True)
    name = Column(String, nullable=False)
    color = Column(String) # Hex code or name
    ideology = Column(String)
    is_player = Column(Boolean, default=False)
    
    # Strategic AI Goals
    monthly_goal = Column(String) # Conquer, Prosper, Defense
    weekly_task = Column(String)  # Current milestone
    goal_target_id = Column(Integer) # City or Officer ID target

    # Resources
    gold_treasury = Column(Integer, default=5000)
    supplies = Column(Integer, default=10000)
    tax_rate = Column(Float, default=0.1)
    
    # Relationships
    officers = relationship("Officer", back_populates="faction")
    cities = relationship("City", back_populates="faction")

class UnitType(Base):
    __tablename__ = 'unit_types'
    
    unit_type_id = Column(Integer, primary_key=True)
    name = Column(String, nullable=False)
    role = Column(SqEnum(UnitRole), nullable=False)
    is_default = Column(Boolean, default=False) # Is this the default unit for the role?
    
    # Stats (from spreadsheet Unit Stats)
    base_health = Column(Float, default=100.0)
    base_attack = Column(Float, default=10.0)
    base_defense = Column(Float, default=0.0)
    base_speed = Column(Float, default=1.0)
    base_mana = Column(Float, default=0.0)
    
    # Costs
    recruit_cost = Column(Float, default=10.0)
    upkeep_cost = Column(Float, default=1.0)
    
    description = Column(String)

class Officer(Base):
    __tablename__ = 'officers'
    
    officer_id = Column(Integer, primary_key=True)
    name = Column(String, nullable=False)
    faction_id = Column(Integer, ForeignKey('factions.faction_id'), nullable=True) # Can be Ronin (None)
    location_id = Column(Integer, ForeignKey('cities.city_id'), nullable=True)
    
    # Flags
    is_player = Column(Boolean, default=False)
    is_commander = Column(Boolean, default=False) # Is a Faction Leader?
    
    # Navigation
    destination_city_id = Column(Integer, ForeignKey('cities.city_id'), nullable=True) # For multi-day travel
    
    # Attributes
    gold = Column(Integer, default=200)
    leadership = Column(Integer, default=50)
    intelligence = Column(Integer, default=50)
    strength = Column(Integer, default=50)
    politics = Column(Integer, default=50)
    charisma = Column(Integer, default=50)
    
    # Progression
    rank = Column(String, default="Volunteer") # Determines Title & Troop Cap
    reputation = Column(Integer, default=0) # Global karma
    current_action_points = Column(Integer, default=3) # Refreshed daily
    max_action_points = Column(Integer, default=3) # Cap (refreshed daily)
    
    # Troops
    troops = Column(Integer, default=0)
    max_troops = Column(Integer, default=1000)
    unit_type_id = Column(Integer, ForeignKey('unit_types.unit_type_id'), nullable=True)

    # Strategic AI Assignments (Leader Orders)
    current_assignment = Column(String)
    assignment_target_id = Column(Integer)
    
    # Stat Allocation
    stat_points = Column(Integer, default=0)
    base_strength = Column(Integer, default=50)
    base_leadership = Column(Integer, default=50)
    base_intelligence = Column(Integer, default=50)
    base_politics = Column(Integer, default=50)
    base_politics = Column(Integer, default=50)
    base_charisma = Column(Integer, default=50)

    # Combat Config
    formation_type = Column(Integer, default=0) # FormationShape enum
    
    # Relationships
    faction = relationship("Faction", back_populates="officers")
    current_city = relationship("City", back_populates="officers", foreign_keys=[location_id])
    unit_type = relationship("UnitType")

class GameState(Base):
    __tablename__ = 'game_state'
    
    save_id = Column(Integer, primary_key=True)
    current_day = Column(Integer, default=1)
    player_id = Column(Integer, ForeignKey('officers.officer_id'))
    
    # Could store global world flags here


class City(Base):
    __tablename__ = 'cities'
    
    city_id = Column(Integer, primary_key=True)
    name = Column(String, nullable=False)
    faction_id = Column(Integer, ForeignKey('factions.faction_id'))
    
    # City Stats (RotTK 8 Style)
    agriculture = Column(Integer, default=100) # Yields Food
    commerce = Column(Integer, default=100)    # Yields Gold
    technology = Column(Integer, default=50)   # Unlocks Unit Tiers
    public_order = Column(Integer, default=80) # Multiplier for yields (0-100)
    defense_level = Column(Integer, default=100) # Gate/Wall HP in Sieges
    max_stats = Column(Integer, default=1000)   # Cap for growth
    
    # Conquest Logic
    is_hq = Column(Integer, default=0) # 0 = False, 1 = True
    decay_turns = Column(Integer, default=0)
    
    # Relationships
    faction = relationship("Faction", back_populates="cities")
    officers = relationship("Officer", back_populates="current_city", foreign_keys="[Officer.location_id]")
    
    # Routes (Outgoing)
    routes = relationship("Route", foreign_keys="[Route.start_city_id]", back_populates="start_city")

class Route(Base):
    __tablename__ = 'routes'
    
    route_id = Column(Integer, primary_key=True)
    start_city_id = Column(Integer, ForeignKey('cities.city_id'), nullable=False)
    end_city_id = Column(Integer, ForeignKey('cities.city_id'), nullable=False)
    
    # Route Stats
    distance = Column(Float, default=1.0) # Days to travel
    route_type = Column(String, default="Road") # Road, River, Mountain Pass
    is_chokepoint = Column(Boolean, default=False)
    
    # Relationships
    start_city = relationship("City", foreign_keys=[start_city_id], back_populates="routes")
    end_city = relationship("City", foreign_keys=[end_city_id])

class BattleMapTemplate(Base):
    __tablename__ = 'battle_map_templates'
    
    template_id = Column(Integer, primary_key=True)
    name = Column(String, nullable=False)
    terrain_type = Column(String) # Plains, Forest, City
    map_type = Column(String) # Open Field, Siege, Ambush
    
    nodes = relationship("BattleNodeTemplate", back_populates="map_template")
    links = relationship("BattleLinkTemplate", back_populates="map_template")

class BattleNodeTemplate(Base):
    __tablename__ = 'battle_node_templates'
    
    node_id = Column(Integer, primary_key=True)
    template_id = Column(Integer, ForeignKey('battle_map_templates.template_id'))
    
    name = Column(String)
    x = Column(Float)
    y = Column(Float)
    node_type = Column(String, default="Standard") # HQ, Depot, Tower, Standard
    
    # Spawn Logic
    is_attacker_spawn = Column(Boolean, default=False)
    is_defender_spawn = Column(Boolean, default=False)
    
    map_template = relationship("BattleMapTemplate", back_populates="nodes")

class BattleLinkTemplate(Base):
    __tablename__ = 'battle_link_templates'
    
    link_id = Column(Integer, primary_key=True)
    template_id = Column(Integer, ForeignKey('battle_map_templates.template_id'))
    source_node_id = Column(Integer, ForeignKey('battle_node_templates.node_id'))
    target_node_id = Column(Integer, ForeignKey('battle_node_templates.node_id'))
    
    distance = Column(Float, default=1.0)
    
    map_template = relationship("BattleMapTemplate", back_populates="links")

class FactionRelation(Base):
    __tablename__ = 'faction_relations'
    
    relation_id = Column(Integer, primary_key=True)
    source_faction_id = Column(Integer, ForeignKey('factions.faction_id'), nullable=False)
    target_faction_id = Column(Integer, ForeignKey('factions.faction_id'), nullable=False)
    
    # -100 (Total War) to 100 (Absolute Ally)
    value = Column(Integer, default=0)
    
    # Relationships (Optional, for navigation if needed)
    source_faction = relationship("Faction", foreign_keys=[source_faction_id])
    target_faction = relationship("Faction", foreign_keys=[target_faction_id])

class PendingBattle(Base):
    __tablename__ = 'pending_battles'
    
    battle_id = Column(Integer, primary_key=True)
    location_id = Column(Integer, ForeignKey('cities.city_id'), nullable=False)
    attacker_faction_id = Column(Integer, ForeignKey('factions.faction_id'), nullable=False)
    source_location_id = Column(Integer, ForeignKey('cities.city_id'), nullable=True)
    leader_id = Column(Integer, ForeignKey('officers.officer_id'), nullable=True)
