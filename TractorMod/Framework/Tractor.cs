using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Netcode;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.TractorMod.Framework.Attachments;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SFarmer = StardewValley.Farmer;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.TractorMod.Framework
{
    /// <summary>The in-game tractor that can be ridden by the player.</summary>
    internal sealed class Tractor : Horse
    {
        /*********
        ** Properties
        *********/
        /// <summary>The unique buff ID for the tractor speed.</summary>
        private readonly int BuffUniqueID = 58012397;

        /// <summary>The number of ticks between each tractor action check.</summary>
        private readonly int TicksPerAction = 12; // roughly five times per second

        /// <summary>Provides translations from the mod's i18n folder.</summary>
        private readonly ITranslationHelper Translation;

        /// <summary>Simplifies access to private game code.</summary>
        private readonly IReflectionHelper Reflection;

        /// <summary>The tractor attachments to apply.</summary>
        private readonly IAttachment[] Attachments;

        /// <summary>The attachment cooldowns in ticks for each rate-limited attachment.</summary>
        private readonly IDictionary<IAttachment, int> AttachmentCooldowns;

        /// <summary>The mod settings.</summary>
        private readonly ModConfig Config;

        /// <summary>The number of ticks since the tractor last checked for an action to perform.</summary>
        private int SkippedActionTicks;

        /// <summary>The trellis crop IDs.</summary>
        private readonly HashSet<int> RaisedSeedCrops = new HashSet<int>();

        /// <summary>Whether the player was riding the tractor during the last tick.</summary>
        private bool WasRiding;

        /// <summary>The rider health to maintain if they're invincible.</summary>
        private int RiderHealth;


        /*********
        ** Accessors
        *********/
        /// <summary>Whether the player is currently riding the tractor.</summary>
        public bool IsRiding => this.rider == Game1.player;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="tractorID">The tractor's unique horse ID.</param>
        /// <param name="tileX">The initial tile X position.</param>
        /// <param name="tileY">The initial tile Y position.</param>
        /// <param name="config">The mod settings.</param>
        /// <param name="attachments">The tractor attachments to apply.</param>
        /// <param name="textureName">The texture asset name to load.</param>
        /// <param name="translation">Provides translations from the mod's i18n folder.</param>
        /// <param name="reflection">Simplifies access to private game code.</param>
        public Tractor(Guid tractorID, int tileX, int tileY, ModConfig config, IEnumerable<IAttachment> attachments, string textureName, ITranslationHelper translation, IReflectionHelper reflection)
            : base(tractorID, tileX, tileY)
        {
            this.Name = typeof(Tractor).Name;
            this.Sprite = new AnimatedSprite(textureName, 0, 32, 32)
            {
                textureUsesFlippedRightForLeft = true,
                loop = true
            };
            this.Config = config;
            this.Attachments = attachments.ToArray();
            this.AttachmentCooldowns = this.Attachments.Where(p => p.RateLimit > this.TicksPerAction).ToDictionary(p => p, p => 0);
            this.Translation = translation;
            this.Reflection = reflection;
        }

        /// <summary>Move the tractor to the given location.</summary>
        /// <param name="location">The game location.</param>
        /// <param name="tile">The tile coordinate in the given location.</param>
        /// <remarks>The default <see cref="Game1.warpCharacter(NPC,GameLocation,Vector2)"/> logic doesn't work in the mines, so this method reimplements it with better logic.</remarks>
        public void SetLocation(GameLocation location, Vector2 tile)
        {
            this.RemoveFromLocation();
            if (!location.characters.Contains(this))
                location.addCharacter(this);
            this.currentLocation = location;

            this.isCharging = false;
            this.speed = 2;
            this.blockedInterval = 0;
            this.position.X = tile.X * Game1.tileSize;
            this.position.Y = tile.Y * Game1.tileSize;
        }

        /// <summary>Update tractor effects and actions in the game.</summary>
        public void Update()
        {
            // track health for invincibility
            if (this.Config.InvincibleOnTractor && this.IsRiding != this.WasRiding)
            {
                if (this.IsRiding)
                    this.RiderHealth = Game1.player.health;
                this.WasRiding = this.IsRiding;
            }

            // apply riding effects
            if (this.IsRiding && Game1.activeClickableMenu == null)
            {
                // apply invincibility
                if (this.Config.InvincibleOnTractor)
                {
                    if (Game1.player.health > this.RiderHealth)
                        this.RiderHealth = Game1.player.health;
                    else
                        Game1.player.health = this.RiderHealth;
                    Game1.player.temporarilyInvincible = false; // disable damage blink effect
                    Game1.player.temporaryInvincibilityTimer = 0;
                }

                // apply tractor buff
                this.UpdateBuff();

                // apply tools
                if (this.UpdateCooldown() && this.IsEnabled())
                    this.UpdateAttachmentEffects();
            }
        }

        /// <summary>Update tractor logic when the player warps to a new location.</summary>
        /// <param name="oldLocation">The former location.</param>
        /// <param name="newLocation">The new location.</param>
        public void UpdateForNewLocation(GameLocation oldLocation, GameLocation newLocation)
        {
            if (this.Config.PassThroughTrellisCrops && this.IsRiding)
                this.SetCropPassthrough(oldLocation, false);
        }

        /// <summary>Draw a radius around the player.</summary>
        /// <param name="spriteBatch">The sprite batch being drawn.</param>
        public void DrawRadius(SpriteBatch spriteBatch)
        {
            bool enabled = this.IsEnabled();

            foreach (Vector2 tile in this.GetTileGrid(Game1.player.getTileLocation(), this.Config.Distance))
            {
                // get tile area in screen pixels
                Rectangle area = new Rectangle((int)(tile.X * Game1.tileSize - Game1.viewport.X), (int)(tile.Y * Game1.tileSize - Game1.viewport.Y), Game1.tileSize, Game1.tileSize);

                // choose tile color
                Color color = enabled ? Color.Green : Color.Red;

                // draw background
                spriteBatch.DrawLine(area.X, area.Y, new Vector2(area.Width, area.Height), color * 0.2f);

                // draw border
                int borderSize = 1;
                Color borderColor = color * 0.5f;
                spriteBatch.DrawLine(area.X, area.Y, new Vector2(area.Width, borderSize), borderColor); // top
                spriteBatch.DrawLine(area.X, area.Y, new Vector2(borderSize, area.Height), borderColor); // left
                spriteBatch.DrawLine(area.X + area.Width, area.Y, new Vector2(borderSize, area.Height), borderColor); // right
                spriteBatch.DrawLine(area.X, area.Y + area.Height, new Vector2(area.Width, borderSize), borderColor); // bottom
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get whether the tractor is toggled on by the player.</summary>
        private bool IsEnabled()
        {
            if (!this.IsRiding)
                return false;

            // automatic mode
            if (!this.Config.Controls.HoldToActivate.Any())
                return true;

            // hold-to-activate mode
            KeyboardState state = Keyboard.GetState();
            return this.Config.Controls.HoldToActivate.Any(button => button.TryGetKeyboard(out Keys key) && state.IsKeyDown(key));
        }

        /// <summary>Apply the tractor buff to the current player.</summary>
        private void UpdateBuff()
        {
            Buff buff = Game1.buffsDisplay.otherBuffs.FirstOrDefault(p => p.which == this.BuffUniqueID);
            if (buff == null)
            {
                buff = new Buff(0, 0, 0, 0, 0, 0, 0, 0, this.Config.MagneticRadius, this.Config.TractorSpeed, 0, 0, 1, "Tractor Power", this.Translation.Get("buff.name")) { which = this.BuffUniqueID };
                Game1.buffsDisplay.addOtherBuff(buff);
            }
            buff.millisecondsDuration = 100;
        }

        /// <summary>Update the attachment cooldown.</summary>
        /// <returns>Returns whether the cooldown has ended.</returns>
        private bool UpdateCooldown()
        {
            this.SkippedActionTicks++;

            if (this.SkippedActionTicks % this.TicksPerAction != 0)
                return false;

            this.SkippedActionTicks = 0;
            return true;
        }

        /// <summary>Apply any effects for the current tractor attachment.</summary>
        private void UpdateAttachmentEffects()
        {
            // get context
            SFarmer player = Game1.player;
            GameLocation location = Game1.currentLocation;
            Tool tool = player.CurrentTool;
            Item item = player.CurrentItem;

            // get active attachments
            IAttachment[] attachments = this.GetApplicableAttachmentsAfterCooldown(player, tool, item, location).ToArray();
            if (!attachments.Any())
                return;

            // get tile grid to affect
            // This must be done outside the temporary interaction block below, since that dismounts
            // the player which changes their position from what the player may expect.
            Vector2[] grid = this.GetTileGrid(Game1.player.getTileLocation(), this.Config.Distance).ToArray();

            // apply tools
            this.TemporarilyFakeInteraction(() =>
            {
                foreach (Vector2 tile in grid)
                {
                    // face tile to avoid game skipping interaction
                    player.Position = new Vector2(tile.X - 1, tile.Y) * Game1.tileSize;
                    player.FacingDirection = 1;

                    // apply attachment effects
                    location.objects.TryGetValue(tile, out SObject tileObj);
                    location.terrainFeatures.TryGetValue(tile, out TerrainFeature tileFeature);
                    foreach (IAttachment attachment in attachments)
                    {
                        if (attachment.Apply(tile, tileObj, tileFeature, Game1.player, tool, item, Game1.currentLocation))
                        {
                            this.ResetCooldown(attachment);
                            break;
                        }
                    }
                }
            });
        }

        /// <summary>Get the attachments which are ready and can be applied to the given tile, after applying cooldown.</summary>
        /// <param name="player">The current player.</param>
        /// <param name="tool">The tool selected by the player (if any).</param>
        /// <param name="item">The item selected by the player (if any).</param>
        /// <param name="location">The current location.</param>
        private IEnumerable<IAttachment> GetApplicableAttachmentsAfterCooldown(SFarmer player, Tool tool, Item item, GameLocation location)
        {
            foreach (IAttachment attachment in this.Attachments)
            {
                // run cooldown
                if (attachment.RateLimit > this.TicksPerAction)
                {
                    int cooldown = this.AttachmentCooldowns[attachment];
                    if (cooldown > this.TicksPerAction)
                    {
                        this.AttachmentCooldowns[attachment] -= this.TicksPerAction;
                        continue;
                    }
                }

                // yield attachment
                if (attachment.IsEnabled(player, tool, item, location))
                    yield return attachment;
            }
        }

        /// <summary>Reset the cooldown for an attachment.</summary>
        /// <param name="attachment">The attachment to reset.</param>
        private void ResetCooldown(IAttachment attachment)
        {
            if (attachment.RateLimit > 0)
                this.AttachmentCooldowns[attachment] = attachment.RateLimit;
        }

        /// <summary>Get a grid of tiles.</summary>
        /// <param name="origin">The center of the grid.</param>
        /// <param name="distance">The number of tiles in each direction to include.</param>
        private IEnumerable<Vector2> GetTileGrid(Vector2 origin, int distance)
        {
            for (int x = -distance; x <= distance; x++)
            {
                for (int y = -distance; y <= distance; y++)
                    yield return new Vector2(origin.X + x, origin.Y + y);
            }
        }

        /// <summary>Remove an NPC from its current location.</summary>
        /// <remarks>The default <see cref="Game1.removeCharacterFromItsLocation"/> logic doesn't work in the mines, so this method reimplements it with better logic.</remarks>
        private void RemoveFromLocation()
        {
            this.currentLocation?.characters.Remove(this); // default logic doesn't support the mines (since they're not in Game1.locations)
            this.currentLocation = null;
        }

        /// <summary>Update all crops in a location to toggle between passable (regardless of trellis) or normal behaviour.</summary>
        /// <param name="location">The location whose crops to update.</param>
        /// <param name="passthrough">Whether to override crop passability.</param>
        private void SetCropPassthrough(GameLocation location, bool passthrough)
        {
            if (location == null)
                return;

            foreach (HoeDirt dirt in location.terrainFeatures.Values.OfType<HoeDirt>())
            {
                if (dirt.crop == null)
                    continue;

                // track which crops have trellises
                if (dirt.crop.raisedSeeds.Value)
                    this.RaisedSeedCrops.Add(dirt.crop.indexOfHarvest.Value);

                // update passthrough
                dirt.crop.raisedSeeds.Value = !passthrough && this.RaisedSeedCrops.Contains(dirt.crop.indexOfHarvest.Value);
            }
        }

        /// <summary>Temporarily dismount and set up the player to interact with a tile, then return it to the previous state afterwards.</summary>
        /// <param name="action">The action to perform.</param>
        private void TemporarilyFakeInteraction(Action action)
        {
            // get references
            SFarmer player = Game1.player;
            NetRef<Horse> mountField = this.Reflection.GetField<NetRef<Horse>>(Game1.player, "netMount").GetValue(); // change value directly to bypass the game's on-dismount logic

            // save current state
            Horse mount = mountField.Value;
            Vector2 mountPosition = this.Position;
            WateringCan wateringCan = player.CurrentTool as WateringCan;
            int waterInCan = wateringCan?.WaterLeft ?? 0;
            float stamina = player.stamina;
            Vector2 position = player.Position;
            int facingDirection = player.FacingDirection;
            int currentToolIndex = player.CurrentToolIndex;
            bool canMove = Game1.player.canMove; // fix player frozen due to animations when performing an action

            // move mount out of the way
            mountField.Value = null;
            this.Position = new Vector2(-5, -5);

            // perform action
            try
            {
                action();
            }
            finally
            {
                // move mount back
                this.Position = mountPosition;
                mountField.Value = mount;

                // restore previous state
                if (wateringCan != null)
                    wateringCan.WaterLeft = waterInCan;
                player.stamina = stamina;
                player.Position = position;
                player.FacingDirection = facingDirection;
                player.CurrentToolIndex = currentToolIndex;
                Game1.player.canMove = canMove;
            }
        }
    }
}
